using System.Numerics;
using Brutal.Numerics;
using BrutalGame.Net.Transport;
using BrutalGame.Sim;
using BrutalGame.Sim.Entities;
using BrutalGame.Terrain;
using Xunit.Abstractions;

namespace BrutalGame.Net.Tests;

/// <summary>Server and clients in one process over <see cref="LoopbackNetwork"/>: deterministic latency and loss.</summary>
public class SessionTests(ITestOutputHelper output)
{
    [Fact]
    public void TwoClients_JoinSeeEachOtherAndReconcileExactly()
    {
        using var session = new Session(latencySteps: 3, loss: 0.0);
        var a = session.AddClient(0);
        var b = session.AddClient(1);
        session.Run(600);

        Assert.True(a.Joined && b.Joined);
        Assert.Equal(2, session.Server.PlayerCount);
        Assert.Equal(2, a.Entities.Count);
        Assert.Equal(2, b.Entities.Count);
        Assert.True(a.Stats.ReconcileCount > 200, $"reconciles {a.Stats.ReconcileCount}");
        Assert.Equal(0, a.Stats.MissingBaselines);
        Assert.Equal(0, session.Server.Stats(0).InputsLate);

        // With no loss the server applied every predicted input: only wire quantisation separates them.
        Assert.True(a.Stats.ReconcileErrorMax < 0.005, $"max error {a.Stats.ReconcileErrorMax * 1000:F3} mm");
        Assert.True(b.Stats.ReconcileErrorMax < 0.005, $"max error {b.Stats.ReconcileErrorMax * 1000:F3} mm");

        var server = session.Server.Sim.Entities;
        var other = session.Server.PlayerEntity(1);
        var remote = Find(a, server.NetId[other.Index]);
        var lag = (a.Entities.Placement[remote].Position - server.Placement[other.Index].Position).Length();
        Assert.True(lag < 10.0, $"remote body {lag:F2} m from the server's now (interpolation delay)");

        output.WriteLine($"a: {a.Stats.ReconcileCount} reconciles, mean {a.Stats.ReconcileErrorMean * 1000:F3} mm, max {a.Stats.ReconcileErrorMax * 1000:F3} mm, slack {a.Stats.Slack}, in {a.Connection.PayloadReceived * 60.0 / 600:F0} B/s");
    }

    [Fact]
    public void Loss_IsCorrectedByReconcileAndRedundantInputs()
    {
        using var session = new Session(latencySteps: 5, loss: 0.1);
        var a = session.AddClient(0);
        session.AddClient(1);
        session.Run(900);

        Assert.True(a.Joined);
        Assert.True(a.Stats.ReconcileCount > 200);
        Assert.True(a.Stats.ReconcileErrorMean < 0.05, $"mean {a.Stats.ReconcileErrorMean * 1000:F2} mm");
        output.WriteLine($"10% loss: missing inputs {session.Server.Stats(0).InputsMissing}, reconcile mean {a.Stats.ReconcileErrorMean * 1000:F3} mm max {a.Stats.ReconcileErrorMax * 1000:F3} mm, over 1 cm {a.Stats.ReconcileOver1Cm}");
    }

    [Fact]
    public void Interest_SendsFarBodiesSlowerAndOtherSoisRarely()
    {
        using var session = new Session(latencySteps: 2, loss: 0.0);
        var sim = session.Server.Sim;
        var spawn = session.Server.SpawnPlacement;
        var near = Spawn(sim, spawn, 500.0);
        var mid = Spawn(sim, spawn, 10000.0);
        var far = Spawn(sim, spawn, 50000.0);
        var moon = sim.Entities.Spawn(EntityKind.Probe, Placement.At(sim.Celestial[2].Surface, new double3(0, sim.Celestial[2].Radius + 10, 0)));

        EntityId[] bodies = [near, mid, far, moon];

        foreach (var id in bodies)
            sim.Entities.Input[id.Index] = new EntityInput { MoveZ = 100 };

        var a = session.AddClient(0);
        session.Run(60);
        var before = new int[4];

        for (var i = 0; i < 4; i++)
            before[i] = session.Server.UpdatesSent(0, bodies[i].Index);

        session.Run(600);
        var sent = new int[4];

        for (var i = 0; i < 4; i++)
            sent[i] = session.Server.UpdatesSent(0, bodies[i].Index) - before[i];

        output.WriteLine($"updates over 10 s: near {sent[0]}, 10 km {sent[1]}, 50 km {sent[2]}, moon {sent[3]}");
        Assert.InRange(sent[0], 290, 300);
        Assert.InRange(sent[1], 95, 101);
        Assert.InRange(sent[2], 9, 11);
        Assert.InRange(sent[3], 1, 3);
        Assert.Equal(4 + 1, a.Entities.Count);
        Assert.Equal(InterestClass.Bubble, Interest.Classify(sim.Frames, spawn, sim.Entities.Placement[near.Index]));
        Assert.Equal(InterestClass.Frame, Interest.Classify(sim.Frames, spawn, sim.Entities.Placement[mid.Index]));
        Assert.Equal(InterestClass.Soi, Interest.Classify(sim.Frames, spawn, sim.Entities.Placement[far.Index]));
        Assert.Equal(InterestClass.System, Interest.Classify(sim.Frames, spawn, sim.Entities.Placement[moon.Index]));

        var stats = session.Server.Stats(0);
        output.WriteLine($"snapshot bytes {stats.SnapshotBytes} over {stats.SnapshotsSent} snapshots, events {stats.EventBytes} B");
    }

    [Fact]
    public void FrameChangesAndEdits_ArriveAsReliableEvents()
    {
        using var session = new Session(latencySteps: 2, loss: 0.2);
        var sim = session.Server.Sim;
        var hopper = Spawn(sim, session.Server.SpawnPlacement, 50.0);
        var a = session.AddClient(0);
        session.Run(120);

        ref var placement = ref sim.Entities.Placement[hopper.Index];
        var inertial = sim.Celestial[1].Inertial;
        Assert.True(PlacementFrames.TryMoveTo(sim.Frames, ref placement, inertial));

        var op = new BrushOp(new Vector3(400f, 20f, 37.5f), 3f, BrushMode.Place, VoxelMaterial.Rock);
        a.RequestEdit(op);
        session.Run(120);

        Assert.Equal(1, a.Stats.FrameChanges);
        var index = Find(a, sim.Entities.NetId[hopper.Index]);
        Assert.Equal(inertial, a.Entities.Placement[index].Frame);
        Assert.Single(a.Edits);
        Assert.Equal(Wire.Messages.Quantise(op), a.Edits[0]);
        Assert.Equal(1, sim.Edits.Length);
    }

    [Fact]
    public void Leave_DespawnsOnOtherClients()
    {
        using var session = new Session(latencySteps: 2, loss: 0.0);
        var a = session.AddClient(0);
        var b = session.AddClient(1);
        session.Run(120);
        Assert.Equal(2, a.Entities.Count);

        b.Disconnect();
        session.Run(60);

        Assert.Equal(1, session.Server.PlayerCount);
        Assert.Equal(1, a.Entities.Count);
        Assert.Equal(1, a.Stats.Despawns);
    }

    /// <summary>CLAUDE.md: zero allocations per tick. Server poll + step with two clients and 40 bodies, after warm-up.</summary>
    [Fact]
    public void ServerTick_AllocatesNothing()
    {
        using var session = new Session(latencySteps: 3, loss: 0.05);
        var sim = session.Server.Sim;

        for (var i = 0; i < 40; i++)
            sim.Entities.Input[Spawn(sim, session.Server.SpawnPlacement, 30.0 * i + 10.0).Index] = new EntityInput { MoveZ = 90, Yaw = 20 };

        session.AddClient(0);
        session.AddClient(1);
        session.Run(300);

        session.Run(300, measureServer: true);
        Assert.Equal(0, session.ServerAllocated);
    }

    private static EntityId Spawn(SimWorld sim, in Placement spawn, double offset)
    {
        var placement = spawn;
        placement.Position += new double3(offset, 0.0, offset * 0.25);
        return sim.Entities.Spawn(EntityKind.Probe, placement);
    }

    private static int Find(NetClient client, uint netId)
    {
        foreach (var index in client.Entities.Alive)
        {
            if (client.Entities.NetId[index] == netId)
                return index;
        }

        throw new InvalidOperationException($"client has no body {netId}");
    }
}

internal sealed class Session : IDisposable
{
    public readonly LoopbackNetwork Network;
    public readonly NetServer Server;
    public long ServerAllocated;

    private readonly SimWorld _sim;
    private readonly LoopbackTransport _serverTransport;
    private readonly List<NetClient> _clients = [];

    public Session(int latencySteps, double loss)
    {
        Network = new LoopbackNetwork(latencySteps, loss, seed: 11);
        _sim = new SimWorld(seed: 1, workerThreads: 1, new TerrainParams());
        _serverTransport = Network.Listen(8);
        Server = new NetServer(_sim, _serverTransport);
        var planet = _sim.Celestial[1];
        Server.SpawnPlacement = Placement.At(planet.Surface, new double3(0.0, planet.Radius + 500.0, 0.0));
    }

    public NetClient AddClient(int pattern)
    {
        var client = new NetClient(Network.Connect(_serverTransport), new TestInput(pattern));
        _clients.Add(client);
        return client;
    }

    /// <summary>Steps of one server tick each; clients get the same 1/60 s frame.</summary>
    public void Run(int steps, bool measureServer = false)
    {
        for (var i = 0; i < steps; i++)
        {
            Network.Now++;
            var before = GC.GetAllocatedBytesForCurrentThread();
            Server.Poll();

            if (Server.PlayerCount > 0)
                Server.Step();

            if (measureServer)
                ServerAllocated += GC.GetAllocatedBytesForCurrentThread() - before;

            foreach (var client in _clients)
                client.Update(1.0 / 60.0);
        }
    }

    public void Dispose() => _sim.Dispose();
}

internal sealed class TestInput(int pattern) : IInputSource
{
    public EntityInput Sample(long tick, in Placement owned)
    {
        var phase = tick / 60.0 * (0.7 + 0.3 * pattern);

        return new EntityInput
        {
            MoveZ = (sbyte)(Math.Sin(phase) > 0 ? 120 : 20),
            MoveX = (sbyte)(50 * Math.Sin(phase * 1.7)),
            Yaw = (sbyte)(80 * Math.Sin(phase * 0.9 + pattern)),
            Pitch = (sbyte)(20 * Math.Cos(phase * 0.5)),
        };
    }
}
