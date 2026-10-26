using System.Numerics;

namespace BrutalGame.Physics.Tests;

public class PhysicsWorldTests
{
    const float Dt = 1f / 60f;
    const int ThreeSeconds = 180;

    internal static StaticMeshShape CreatePlane(float halfSize = 10f)
    {
        Vector3[] vertices =
        [
            new(-halfSize, 0f, -halfSize),
            new(-halfSize, 0f, halfSize),
            new(halfSize, 0f, halfSize),
            new(halfSize, 0f, -halfSize),
        ];
        uint[] indices = [0, 1, 2, 0, 2, 3];
        return StaticMeshShape.Create(vertices, indices)!;
    }

    static void StepFor(PhysicsWorld world, int steps)
    {
        for (int i = 0; i < steps; i++)
            world.Step(Dt);
    }

    [Fact]
    public void SphereComesToRestOnMeshPlane()
    {
        using var world = new PhysicsWorld();
        using (var plane = CreatePlane())
            world.AddStatic(plane, Vector3.Zero);

        var sphere = world.AddDynamicSphere(new Vector3(0f, 5f, 0f), 0.5f);
        StepFor(world, ThreeSeconds);

        var p = world.GetPosition(sphere);
        Assert.InRange(p.Y, 0.45f, 0.55f);
        Assert.True(world.GetLinearVelocity(sphere).Length() < 0.05f);
    }

    [Fact]
    public void RayDownHitsPlane()
    {
        using var world = new PhysicsWorld();
        using var plane = CreatePlane();
        var body = world.AddStatic(plane, Vector3.Zero);

        var hit = world.CastRay(new Vector3(1f, 10f, 2f), -Vector3.UnitY, 100f);

        Assert.True(hit.Hit);
        Assert.Equal(body, hit.Body);
        Assert.Equal(0f, hit.Point.Y, 1e-4f);
        Assert.Equal(10f, hit.Distance, 1e-4f);
        Assert.True(Vector3.Dot(hit.Normal, Vector3.UnitY) > 0.999f);
        Assert.False(world.CastRay(new Vector3(1f, 10f, 2f), -Vector3.UnitY, 5f).Hit);
        Assert.False(world.CastRay(new Vector3(50f, 10f, 0f), -Vector3.UnitY, 100f).Hit);
    }

    [Fact]
    public void RayHitsDynamicBodiesBeforePlane()
    {
        using var world = new PhysicsWorld();
        using var plane = CreatePlane();
        world.AddStatic(plane, Vector3.Zero);
        var sphere = world.AddDynamicSphere(new Vector3(0f, 3f, 0f), 0.5f);
        var box = world.AddDynamicBox(new Vector3(4f, 3f, 0f), new Vector3(0.5f));

        var sphereHit = world.CastRay(new Vector3(0f, 10f, 0f), -Vector3.UnitY, 100f);
        var boxHit = world.CastRay(new Vector3(4f, 10f, 0f), -Vector3.UnitY, 100f);

        Assert.Equal(sphere, sphereHit.Body);
        Assert.Equal(3.5f, sphereHit.Point.Y, 1e-3f);
        Assert.Equal(box, boxHit.Body);
        Assert.Equal(3.5f, boxHit.Point.Y, 1e-3f);
        Assert.True(Vector3.Dot(boxHit.Normal, Vector3.UnitY) > 0.999f);
    }

    [Fact]
    public void RemoveUpdatesBodyCount()
    {
        using var world = new PhysicsWorld();
        using var plane = CreatePlane();
        var ground = world.AddStatic(plane, Vector3.Zero);
        var a = world.AddDynamicSphere(new Vector3(0f, 2f, 0f), 0.5f);
        var b = world.AddDynamicBox(new Vector3(3f, 2f, 0f), new Vector3(0.5f));
        Assert.Equal(3, world.BodyCount);

        world.Remove(a);
        Assert.Equal(2, world.BodyCount);

        world.Remove(a);
        world.Remove(default);
        Assert.Equal(2, world.BodyCount);

        world.Remove(ground);
        Assert.Equal(1, world.BodyCount);
        Assert.False(world.CastRay(new Vector3(0f, 10f, 0f), -Vector3.UnitY, 100f).Hit);

        StepFor(world, 10);
        world.Remove(b);
        Assert.Equal(0, world.BodyCount);

        var c = world.AddDynamicSphere(new Vector3(0f, 2f, 0f), 0.5f);
        Assert.True(c.IsValid);
        Assert.Equal(1, world.BodyCount);
        Assert.False(default(BodyHandle).IsValid);
    }

    [Fact]
    public void StaticMeshShapeCreateIsThreadSafe()
    {
        const int Threads = 8;
        const int PerThread = 16;
        var shapes = new StaticMeshShape?[Threads * PerThread];

        Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, t =>
        {
            for (int i = 0; i < PerThread; i++)
            {
                var (vertices, indices) = TerrainMesh.Grid(32, 1f, (t * PerThread + i) * 0.1f);
                shapes[t * PerThread + i] = StaticMeshShape.Create(vertices, indices);
            }
        });

        using var world = new PhysicsWorld();

        for (int i = 0; i < shapes.Length; i++)
        {
            Assert.NotNull(shapes[i]);
            Assert.Equal(32 * 32 * 2, shapes[i]!.TriangleCount);
            world.AddStatic(shapes[i]!, new Vector3(i * 40f, 0f, 0f));
            shapes[i]!.Dispose();
        }

        world.Step(Dt);
        Assert.Equal(shapes.Length, world.BodyCount);
        Assert.True(world.CastRay(new Vector3(5 * 40f + 16f, 50f, 16f), -Vector3.UnitY, 100f).Hit);
    }

    [Fact]
    public void CreateReturnsNullWithoutTriangles()
    {
        Vector3[] vertices = [Vector3.Zero, Vector3.UnitX, Vector3.UnitX * 2f];
        Assert.Null(StaticMeshShape.Create(vertices, []));
        Assert.Null(StaticMeshShape.Create(vertices, [0, 1, 2]));
        Assert.Null(StaticMeshShape.Create(vertices, [0, 0, 1]));
    }

    [Fact]
    public void BoxRestsOnPlaneWithoutSinking()
    {
        using var world = new PhysicsWorld();
        using (var plane = CreatePlane())
            world.AddStatic(plane, Vector3.Zero);

        var box = world.AddDynamicBox(new Vector3(0f, 2f, 0f), new Vector3(0.5f));
        StepFor(world, ThreeSeconds);
        float settled = world.GetPosition(box).Y;
        StepFor(world, ThreeSeconds);

        Assert.InRange(settled, 0.45f, 0.55f);
        Assert.InRange(world.GetPosition(box).Y, settled - 0.005f, settled + 0.005f);
        Assert.True(Vector3.Dot(Vector3.Transform(Vector3.UnitY, world.GetRotation(box)), Vector3.UnitY) > 0.999f);
    }

    [Fact]
    public void ForcesMoveBodies()
    {
        using var world = new PhysicsWorld();
        var sphere = world.AddDynamicSphere(Vector3.Zero, 0.5f);
        float mass = 1000f * 4f / 3f * MathF.PI * 0.125f;

        for (int i = 0; i < 60; i++)
        {
            world.AddForce(sphere, new Vector3(0f, 9.81f * mass, 0f));
            world.AddForceAtPoint(sphere, new Vector3(mass, 0f, 0f), world.GetPosition(sphere));
            world.Step(Dt);
        }

        var v = world.GetLinearVelocity(sphere);
        Assert.Equal(0f, v.Y, 0.05f);
        Assert.Equal(1f, v.X, 0.05f);
    }

    [Fact]
    public void StepAndCastRayAreAllocationFree()
    {
        using var world = new PhysicsWorld();
        using (var plane = CreatePlane())
            world.AddStatic(plane, Vector3.Zero);

        for (int i = 0; i < 20; i++)
            world.AddDynamicSphere(new Vector3(i % 5 - 2f, 1f + i, i / 5 - 2f), 0.4f);

        StepFor(world, 30);
        world.CastRay(new Vector3(0f, 10f, 0f), -Vector3.UnitY, 100f);

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 120; i++)
        {
            world.Step(Dt);
            world.CastRay(new Vector3(0f, 10f, 0f), -Vector3.UnitY, 100f);
            _ = world.LastStepMs;
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
