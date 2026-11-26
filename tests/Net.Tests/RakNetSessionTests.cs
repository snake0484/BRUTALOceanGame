using System.Diagnostics;
using Brutal.Numerics;
using BrutalGame.Net.Transport;
using BrutalGame.Sim;
using BrutalGame.Sim.Entities;
using BrutalGame.Terrain;
using Xunit.Abstractions;

namespace BrutalGame.Net.Tests;

/// <summary>The real transport: a server and two clients over localhost UDP sockets in one process, in real time.</summary>
public class RakNetSessionTests(ITestOutputHelper output)
{
    [Fact]
    public void TwoClients_ExchangeSnapshotsOverLocalhost()
    {
        var port = (ushort)(41000 + Environment.ProcessId % 2000);
        using var sim = new SimWorld(seed: 3, workerThreads: 1, new TerrainParams());
        using var serverTransport = RakNetTransport.Listen(port, 4);
        var server = new NetServer(sim, serverTransport);
        var planet = sim.Celestial[1];
        server.SpawnPlacement = Placement.At(planet.Surface, new double3(0.0, planet.Radius + 500.0, 0.0));

        using var ta = RakNetTransport.Connect("127.0.0.1", port);
        using var tb = RakNetTransport.Connect("127.0.0.1", port);
        var a = new NetClient(ta, new TestInput(0));
        var b = new NetClient(tb, new TestInput(1));

        var clock = Stopwatch.StartNew();
        var last = 0.0;

        while (clock.Elapsed.TotalSeconds < 6.0 && (a.Stats.Snapshots < 90 || b.Stats.Snapshots < 90))
        {
            var now = clock.Elapsed.TotalSeconds;
            server.Poll();

            if (server.PlayerCount > 0)
            {
                var ticks = sim.Advance(now - last);

                for (var i = 0; i < ticks; i++)
                    server.Step();
            }

            a.Update(now - last);
            b.Update(now - last);
            last = now;
            Assert.False(a.Failed || b.Failed);
            Thread.Sleep(1);
        }

        output.WriteLine($"a: snapshots {a.Stats.Snapshots}, reconciles {a.Stats.ReconcileCount}, mean {a.Stats.ReconcileErrorMean * 1000:F3} mm, in {a.Connection.WireReceived} B wire / {a.Connection.PayloadReceived} B payload");
        Assert.True(a.Joined && b.Joined);
        Assert.Equal(2, server.PlayerCount);
        Assert.True(a.Stats.Snapshots >= 90 && b.Stats.Snapshots >= 90, $"{a.Stats.Snapshots} / {b.Stats.Snapshots} snapshots");
        Assert.Equal(2, a.Entities.Count);
        Assert.Equal(2, b.Entities.Count);
        Assert.True(a.Stats.ReconcileCount > 30);
        Assert.True(a.Connection.WireReceived >= a.Connection.PayloadReceived);
    }
}
