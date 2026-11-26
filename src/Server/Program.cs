using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Brutal.Numerics;
using BrutalGame.Net;
using BrutalGame.Net.Transport;
using BrutalGame.Sim;
using BrutalGame.Sim.Entities;
using BrutalGame.Terrain;

namespace BrutalGame.Server;

/// <summary>
/// Headless dedicated server: a <see cref="SimWorld"/> behind a <see cref="NetServer"/> on RakNet. No renderer, window or
/// Engine reference. Game time stops while nobody is connected (frames-and-zones, Time).
/// </summary>
internal static partial class Program
{
    private static int Main(string[] args)
    {
        var options = ServerOptions.Parse(args);

        if (options is null)
        {
            Console.WriteLine("BRUTALServer --port 7777 --seed 1 [--ticks N] [--max-players 64] [--npcs 48] [--exit-when-empty]");
            return 2;
        }

        if (OperatingSystem.IsWindows())
            TimeBeginPeriod(1);

        using var sim = new SimWorld(options.Seed, workerThreads: 2);
        using var transport = RakNetTransport.Listen(options.Port, options.MaxPlayers);
        var server = new NetServer(sim, transport);
        var planet = sim.Celestial[1];
        server.SpawnPlacement = Placement.At(planet.Surface, new double3(0.0, planet.Radius + 500.0, 0.0));
        var npcs = new NpcScript(sim, server.SpawnPlacement, options.Npcs);

        Console.WriteLine($"server port={options.Port} seed={options.Seed} npcs={npcs.Count} maxPlayers={options.MaxPlayers} ticks={options.Ticks}");

        var log = new ServerLog(server);
        var clock = Stopwatch.StartNew();
        var last = clock.Elapsed.TotalSeconds;
        var ticksRun = 0L;
        var hadPlayers = false;

        while (options.Ticks == 0 || ticksRun < options.Ticks)
        {
            server.Poll();
            var now = clock.Elapsed.TotalSeconds;
            var frame = now - last;
            last = now;

            if (server.PlayerCount > 0)
            {
                hadPlayers = true;
                var ticks = sim.Advance(frame);

                for (var i = 0; i < ticks; i++)
                {
                    npcs.Drive();
                    var start = Stopwatch.GetTimestamp();
                    server.Step();
                    log.Tick(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    ticksRun++;
                }
            }
            else if (hadPlayers && options.ExitWhenEmpty)
            {
                break;
            }

            log.Maybe(now, npcs.FrameHops);
            Thread.Sleep(1);
        }

        log.Summary(npcs.FrameHops);

        for (var slot = 0; slot < server.MaxPlayers; slot++)
        {
            if (server.IsJoined(slot))
                server.Kick(slot);
        }

        transport.Dispose();
        return 0;
    }

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint milliseconds);
}

internal sealed class ServerOptions
{
    public ushort Port = 7777;
    public ulong Seed = 1;
    public long Ticks;
    public int MaxPlayers = 64;
    public int Npcs = 48;
    public bool ExitWhenEmpty;

    public static ServerOptions? Parse(string[] args)
    {
        var options = new ServerOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var next = i + 1 < args.Length ? args[i + 1] : null;

            switch (args[i])
            {
                case "--port" when next is not null:
                    options.Port = ushort.Parse(next, CultureInfo.InvariantCulture);
                    i++;
                    break;

                case "--seed" when next is not null:
                    options.Seed = ulong.Parse(next, CultureInfo.InvariantCulture);
                    i++;
                    break;

                case "--ticks" when next is not null:
                    options.Ticks = long.Parse(next, CultureInfo.InvariantCulture);
                    i++;
                    break;

                case "--max-players" when next is not null:
                    options.MaxPlayers = int.Parse(next, CultureInfo.InvariantCulture);
                    i++;
                    break;

                case "--npcs" when next is not null:
                    options.Npcs = int.Parse(next, CultureInfo.InvariantCulture);
                    i++;
                    break;

                case "--exit-when-empty":
                    options.ExitWhenEmpty = true;
                    break;

                default:
                    return null;
            }
        }

        return options;
    }
}

/// <summary>Once a second: per client bytes/s out (payload and wire), RTT, known bodies, input health; tick ms.</summary>
internal sealed class ServerLog(NetServer server)
{
    private readonly long[] _lastPayload = new long[server.MaxPlayers];
    private readonly long[] _lastWire = new long[server.MaxPlayers];
    private readonly double[] _payloadRate = new double[server.MaxPlayers];
    private readonly double[] _wireRate = new double[server.MaxPlayers];
    private readonly int[] _samples = new int[server.MaxPlayers];
    private double _lastLog = -1.0;
    private double _tickMsSum, _tickMsMax, _tickMsTotalMax;
    private int _tickCount;
    private long _ticksTotal;
    private double _tickMsTotal;

    public void Tick(double ms)
    {
        _tickMsSum += ms;
        _tickMsMax = Math.Max(_tickMsMax, ms);
        _tickCount++;
        _ticksTotal++;
        _tickMsTotal += ms;
    }

    public void Maybe(double now, int hops)
    {
        if (_lastLog < 0.0)
        {
            _lastLog = now;
            return;
        }

        var span = now - _lastLog;

        if (span < 1.0)
            return;

        _lastLog = now;

        if (server.PlayerCount == 0)
        {
            Array.Clear(_lastPayload);
            Array.Clear(_lastWire);
            return;
        }

        var avg = _tickCount > 0 ? _tickMsSum / _tickCount : 0.0;
        Console.WriteLine($"tick={server.Sim.Clock.TickIndex} players={server.PlayerCount} entities={server.Sim.Entities.Count} tickMs avg={avg:F3} max={_tickMsMax:F3} hops={hops}");
        _tickMsTotalMax = Math.Max(_tickMsTotalMax, _tickMsMax);
        _tickMsSum = _tickMsMax = 0.0;
        _tickCount = 0;

        for (var slot = 0; slot < server.MaxPlayers; slot++)
        {
            if (!server.IsJoined(slot))
                continue;

            var stats = server.Stats(slot);
            var payload = stats.SnapshotBytes + stats.EventBytes;
            var wire = stats.Connection.WireSent;

            if (_lastPayload[slot] > 0)
            {
                var payloadRate = (payload - _lastPayload[slot]) / span;
                var wireRate = (wire - _lastWire[slot]) / span;
                _payloadRate[slot] += payloadRate;
                _wireRate[slot] += wireRate;
                _samples[slot]++;
                Console.WriteLine($"  client {slot}: out {payloadRate:F0} B/s payload, {wireRate:F0} B/s wire, rtt {stats.Connection.RoundTripMs} ms, known {stats.KnownEntities}, snapshots {stats.SnapshotsSent}, inputs late {stats.InputsLate} missing {stats.InputsMissing}, slack {stats.Slack}");
            }

            _lastPayload[slot] = payload;
            _lastWire[slot] = wire;
        }
    }

    public void Summary(int hops)
    {
        var avg = _ticksTotal > 0 ? _tickMsTotal / _ticksTotal : 0.0;
        Console.WriteLine($"SUMMARY server ticks={_ticksTotal} tickMsAvg={avg:F3} tickMsMax={_tickMsTotalMax:F3} frameHops={hops}");

        for (var slot = 0; slot < server.MaxPlayers; slot++)
        {
            if (_samples[slot] == 0)
                continue;

            Console.WriteLine($"SUMMARY server client={slot} outPayloadBps={_payloadRate[slot] / _samples[slot]:F0} outWireBps={_wireRate[slot] / _samples[slot]:F0}");
        }
    }
}
