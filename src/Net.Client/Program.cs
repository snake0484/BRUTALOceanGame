using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using BrutalGame.Net.Transport;
using BrutalGame.Sim.Entities;
using BrutalGame.Terrain;

namespace BrutalGame.Net.Client;

/// <summary>
/// Headless test client: connects, flies its owned probe on a scripted input pattern, keeps the interpolation buffer for
/// everyone else and reconciles its own probe. Logs bytes/s in and reconcile error once a second, then a SUMMARY line.
/// </summary>
internal static partial class Program
{
    private static int Main(string[] args)
    {
        var options = ClientOptions.Parse(args);

        if (options is null)
        {
            Console.WriteLine("BRUTALNetClient --connect 127.0.0.1:7777 [--seconds 20] [--pattern 0] [--sim-loss 0.02] [--sim-ping 40] [--edit]");
            return 2;
        }

        if (OperatingSystem.IsWindows())
            TimeBeginPeriod(1);

        using var transport = RakNetTransport.Connect(options.Host, options.Port);

        if (options.SimLoss > 0f || options.SimPing > 0)
        {
            var active = transport.Simulate(options.SimLoss, options.SimPing, (ushort)(options.SimPing / 4));
            Console.WriteLine($"network simulator loss={options.SimLoss} ping+={options.SimPing} active={active}");
        }

        var client = new NetClient(transport, new ScriptedInput(options.Pattern));
        var clock = Stopwatch.StartNew();
        var last = 0.0;
        var log = new ClientLog(client, options.Pattern);
        var edited = false;

        while (clock.Elapsed.TotalSeconds < options.Seconds + 10.0)
        {
            var now = clock.Elapsed.TotalSeconds;
            client.Update(now - last);
            last = now;

            if (client.Failed)
            {
                Console.WriteLine("connect failed");
                return 1;
            }

            if (client.Joined)
            {
                if (options.Edit && !edited && log.JoinedSeconds(now) > 2.0)
                {
                    client.RequestEdit(new BrushOp(new Vector3(400f, 20f, 37.5f), 3f, BrushMode.Place, VoxelMaterial.Rock));
                    edited = true;
                }

                if (log.JoinedSeconds(now) >= options.Seconds)
                    break;
            }

            log.Maybe(now);
            Thread.Sleep(1);
        }

        log.Summary();
        client.Disconnect();
        Thread.Sleep(200);
        return client.Joined || log.Joined ? 0 : 1;
    }

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint milliseconds);
}

/// <summary>Deterministic flying pattern per client: thrust with a varying turn, so prediction has real work to do.</summary>
internal sealed class ScriptedInput(int pattern) : IInputSource
{
    public EntityInput Sample(long tick, in Placement owned)
    {
        var t = tick / 60.0;
        var phase = t * (0.5 + 0.17 * pattern);

        return new EntityInput
        {
            MoveZ = (sbyte)(100 * Math.Sin(phase) > 20 ? 110 : 30),
            MoveX = (sbyte)(60 * Math.Sin(phase * 1.3 + pattern)),
            MoveY = (sbyte)(20 * Math.Cos(phase * 0.6)),
            Yaw = (sbyte)(90 * Math.Sin(phase * 0.8 + 1.0)),
            Roll = (sbyte)(30 * Math.Sin(phase * 0.4)),
        };
    }
}

internal sealed class ClientOptions
{
    public string Host = "127.0.0.1";
    public ushort Port = 7777;
    public double Seconds = 20.0;
    public int Pattern;
    public float SimLoss;
    public ushort SimPing;
    public bool Edit;

    public static ClientOptions? Parse(string[] args)
    {
        var options = new ClientOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var next = i + 1 < args.Length ? args[i + 1] : null;

            switch (args[i])
            {
                case "--connect" when next is not null:
                    var colon = next.LastIndexOf(':');

                    if (colon > 0)
                    {
                        options.Host = next[..colon];
                        options.Port = ushort.Parse(next[(colon + 1)..], CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        options.Host = next;
                    }

                    i++;
                    break;

                case "--seconds" when next is not null:
                    options.Seconds = double.Parse(next, CultureInfo.InvariantCulture);
                    i++;
                    break;

                case "--pattern" when next is not null:
                    options.Pattern = int.Parse(next, CultureInfo.InvariantCulture);
                    i++;
                    break;

                case "--sim-loss" when next is not null:
                    options.SimLoss = float.Parse(next, CultureInfo.InvariantCulture);
                    i++;
                    break;

                case "--sim-ping" when next is not null:
                    options.SimPing = ushort.Parse(next, CultureInfo.InvariantCulture);
                    i++;
                    break;

                case "--edit":
                    options.Edit = true;
                    break;

                default:
                    return null;
            }
        }

        return options;
    }
}

/// <summary>Once a second: bytes/s in, reconcile error, bodies tracked; totals at the end.</summary>
internal sealed class ClientLog(NetClient client, int pattern)
{
    private double _joinedAt = -1.0;
    private double _lastLog;
    private long _lastPayload, _lastWire, _lastWireOut;
    private ClientStats _lastStats;
    private double _payloadSum, _wireSum, _wireOutSum;
    private int _samples;

    public bool Joined => _joinedAt >= 0.0;

    public double JoinedSeconds(double now)
    {
        if (_joinedAt < 0.0 && client.Joined)
        {
            _joinedAt = now;
            _lastLog = now;
        }

        return _joinedAt < 0.0 ? 0.0 : now - _joinedAt;
    }

    public void Maybe(double now)
    {
        if (!client.Joined || now - _lastLog < 1.0)
            return;

        var span = now - _lastLog;
        _lastLog = now;
        var connection = client.Connection;
        var stats = client.Stats;
        var payload = (connection.PayloadReceived - _lastPayload) / span;
        var wire = (connection.WireReceived - _lastWire) / span;
        var wireOut = (connection.WireSent - _lastWireOut) / span;
        var count = stats.ReconcileCount - _lastStats.ReconcileCount;
        var mean = count > 0 ? (stats.ReconcileErrorSum - _lastStats.ReconcileErrorSum) / count : 0.0;

        if (_lastPayload > 0)
        {
            _payloadSum += payload;
            _wireSum += wire;
            _wireOutSum += wireOut;
            _samples++;
        }

        Console.WriteLine($"client {client.Slot} tick={client.Tick} in {payload:F0} B/s payload, {wire:F0} B/s wire, out {wireOut:F0} B/s wire, rtt {connection.RoundTripMs} ms, loss {connection.PacketLoss:P1}, bodies {client.Entities.Count}, reconcile n={count} mean={mean * 1000.0:F3} mm, slack {stats.Slack}, extrapolated {stats.Extrapolated - _lastStats.Extrapolated}, edits {client.Edits.Count}");
        _lastPayload = connection.PayloadReceived;
        _lastWire = connection.WireReceived;
        _lastWireOut = connection.WireSent;
        _lastStats = stats;
    }

    public void Summary()
    {
        var stats = client.Stats;
        var n = Math.Max(1, _samples);
        Console.WriteLine($"SUMMARY client={client.Slot} pattern={pattern} seconds={_samples} inPayloadBps={_payloadSum / n:F0} inWireBps={_wireSum / n:F0} outWireBps={_wireOutSum / n:F0} " +
            $"reconciles={stats.ReconcileCount} reconcileMeanMm={stats.ReconcileErrorMean * 1000.0:F3} reconcileMaxMm={stats.ReconcileErrorMax * 1000.0:F3} over1cm={stats.ReconcileOver1Cm} " +
            $"snapshots={stats.Snapshots} spawns={stats.Spawns} despawns={stats.Despawns} frameChanges={stats.FrameChanges} missingBaselines={stats.MissingBaselines} " +
            $"interpolated={stats.Interpolated} extrapolated={stats.Extrapolated} resyncs={stats.Resyncs} edits={client.Edits.Count} bodies={client.Entities.Count}");
    }
}
