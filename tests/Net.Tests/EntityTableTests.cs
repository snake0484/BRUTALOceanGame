using Brutal.Numerics;
using BrutalGame.Frames;
using BrutalGame.Sim.Entities;

namespace BrutalGame.Net.Tests;

public class EntityTableTests
{
    [Fact]
    public void SpawnDespawn_ReusesSlotsWithNewGenerations()
    {
        var table = new EntityTable(4);
        var a = table.Spawn(EntityKind.Probe, Placement.At(new FrameId(1), double3.UnitX));
        var b = table.Spawn(EntityKind.Ship, default, owner: 3);

        Assert.Equal(2, table.Count);
        Assert.True(table.IsAlive(a));
        Assert.Equal(3, table.Owner[b.Index]);
        Assert.NotEqual(table.NetId[a.Index], table.NetId[b.Index]);
        Assert.NotEqual(0u, table.NetId[a.Index]);

        Assert.True(table.Despawn(a));
        Assert.False(table.Despawn(a));
        Assert.False(table.IsAlive(a));
        Assert.Equal(1, table.Count);
        Assert.Equal(b.Index, table.Alive[0]);

        var c = table.Spawn(EntityKind.Probe, default, netId: 900);
        Assert.Equal(a.Index, c.Index);
        Assert.NotEqual(a.Generation, c.Generation);
        Assert.False(table.IsAlive(a));
        Assert.Equal(900u, table.NetId[c.Index]);
    }

    [Fact]
    public void Full_Throws()
    {
        var table = new EntityTable(1);
        table.Spawn(EntityKind.Probe, default);
        Assert.Throws<InvalidOperationException>(() => table.Spawn(EntityKind.Probe, default));
    }

    [Fact]
    public void SpawnDespawn_AllocateNothing()
    {
        var table = new EntityTable(256);
        var ids = new EntityId[256];
        var placement = Placement.At(new FrameId(0), double3.Zero);

        Churn(table, ids, placement);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Churn(table, ids, placement);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static void Churn(EntityTable table, EntityId[] ids, in Placement placement)
    {
        for (var round = 0; round < 10; round++)
        {
            for (var i = 0; i < ids.Length; i++)
                ids[i] = table.Spawn(EntityKind.Probe, placement);

            for (var i = 0; i < ids.Length; i += 2)
                table.Despawn(ids[i]);

            for (var i = 1; i < ids.Length; i += 2)
                table.Despawn(ids[i]);
        }
    }

    [Fact]
    public void Probe_IsDeterministicAndTurns()
    {
        var a = Placement.At(new FrameId(0), double3.Zero);
        var b = a;
        var input = new EntityInput { MoveZ = 127, Yaw = 64 };

        for (var i = 0; i < 120; i++)
        {
            ProbeMotion.Step(ref a, input, 1.0 / 60.0);
            ProbeMotion.Step(ref b, input, 1.0 / 60.0);
        }

        Assert.Equal(a.Position, b.Position);
        Assert.Equal(a.Rotation, b.Rotation);
        Assert.True(a.Position.Length() > 5.0);
        Assert.True(Math.Abs(a.Position.X) > 0.5, "yaw bends the path");
    }
}
