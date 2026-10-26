using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Brutal.JoltApi;
using Brutal.Numerics;

namespace BrutalGame.Physics;

/// <summary>
/// Jolt physics world. Every member is main-thread only (the thread that constructed it); nothing here may be called
/// concurrently with <see cref="Step"/>. Only <see cref="StaticMeshShape.Create"/> is meant for worker threads.
/// <para>
/// Broad-phase: static bodies added since the last optimise are batched; <see cref="Step"/> calls
/// <c>OptimizeBroadPhase</c> before simulating once <see cref="OptimizeAfterStatics"/> have accumulated (or on the
/// first step after any static was added). That cost is included in <see cref="LastStepMs"/>.
/// </para>
/// </summary>
public sealed class PhysicsWorld : IDisposable
{
    public const int OptimizeAfterStatics = 64;

    const uint MaxPhysicsJobs = 2048;
    const uint MaxPhysicsBarriers = 8;
    const uint TempAllocatorBytes = 32 * 1024 * 1024;
    const float CollisionStepDt = 1f / 60f;
    const uint BodyIndexMask = 0x7FFFFF;
    const uint InvalidJoltId = 0xFFFFFFFF;
    const float MaxBoxConvexRadius = 0.05f;
    const float RayFractionMiss = 1f + 1.1920929e-7f;

    enum BodyKind : byte
    {
        None,
        StaticMesh,
        Sphere,
        Box,
    }

    readonly BroadPhaseLayerInterfaceTable _broadPhaseLayers;
    readonly ObjectLayerPairFilterTable _layerPairFilter;
    readonly ObjectVsBroadPhaseLayerFilterTable _objectVsBroadPhase;
    readonly TempAllocatorImplWithMallocFallback _tempAllocatorImpl;
    readonly JobSystemThreadPool _jobPool;
    readonly TempAllocator _tempAllocator;
    readonly JobSystem _jobSystem;
    readonly PhysicsSystem _system;
    readonly BodyInterface _bodies;
    readonly NarrowPhaseQuery _narrowPhase;
    readonly BodyLockInterfaceNoLock _bodyLocks;
    readonly int _ownerThread;

    readonly uint[] _joltIds;
    readonly BodyKind[] _kinds;
    readonly Vector3[] _shapeData;
    readonly TriangleBvh?[] _bvhs;
    readonly Body[] _dynamicBodies; // Jolt Body pointers are stable until DestroyBody; lock-free reads for managed ray casts
    readonly int[] _denseSlots;
    readonly uint[] _staticIds;
    readonly Vector3[] _staticMin;
    readonly Vector3[] _staticMax;
    readonly uint[] _dynamicIds;

    int _staticCount;
    int _dynamicCount;
    int _staticsSinceOptimize;
    bool _optimizedOnce;
    bool _disposed;

    public int BodyCount => _staticCount + _dynamicCount;
    public float LastStepMs { get; private set; }
    internal PhysicsUpdateError LastStepError { get; private set; }
    internal int WorkerThreads { get; }

    public PhysicsWorld(int maxBodies = 65536, int workerThreads = -1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBodies, 1);
        JoltRuntime.EnsureInitialized();
        _ownerThread = Environment.CurrentManagedThreadId;
        WorkerThreads = workerThreads < 0 ? Math.Max(1, Environment.ProcessorCount - 2) : workerThreads;

        _broadPhaseLayers = BroadPhaseLayerInterfaceTable.Create(ObjectLayers.Count, BroadPhaseLayers.Count);
        _broadPhaseLayers.MapObjectToBroadPhaseLayer(ObjectLayers.NonMoving, BroadPhaseLayers.NonMoving);
        _broadPhaseLayers.MapObjectToBroadPhaseLayer(ObjectLayers.Moving, BroadPhaseLayers.Moving);

        _layerPairFilter = ObjectLayerPairFilterTable.Create(ObjectLayers.Count);
        _layerPairFilter.EnableCollision(ObjectLayers.NonMoving, ObjectLayers.Moving);
        _layerPairFilter.EnableCollision(ObjectLayers.Moving, ObjectLayers.Moving);

        _objectVsBroadPhase = ObjectVsBroadPhaseLayerFilterTable.Create(_broadPhaseLayers, BroadPhaseLayers.Count, _layerPairFilter, ObjectLayers.Count);

        _tempAllocatorImpl = TempAllocatorImplWithMallocFallback.Create(TempAllocatorBytes);
        _tempAllocator = _tempAllocatorImpl;
        _jobPool = JobSystemThreadPool.Create(MaxPhysicsJobs, MaxPhysicsBarriers, WorkerThreads);
        _jobSystem = _jobPool;

        uint max = (uint)maxBodies;
        _system = PhysicsSystem.Create();
        _system.Init(max, 0, max, Math.Max(1024u, max / 4), _broadPhaseLayers, _objectVsBroadPhase, _layerPairFilter);
        _system.SetGravity(new float3(0f, -9.81f, 0f));
        _bodies = _system.GetBodyInterface();
        _narrowPhase = _system.GetNarrowPhaseQuery();
        _bodyLocks = _system.GetBodyLockInterfaceNoLock();

        _joltIds = new uint[maxBodies];
        _kinds = new BodyKind[maxBodies];
        _shapeData = new Vector3[maxBodies];
        _bvhs = new TriangleBvh?[maxBodies];
        _dynamicBodies = new Body[maxBodies];
        _denseSlots = new int[maxBodies];
        _staticIds = new uint[maxBodies];
        _staticMin = new Vector3[maxBodies];
        _staticMax = new Vector3[maxBodies];
        _dynamicIds = new uint[maxBodies];
    }

    /// <summary>Advances one fixed tick. Allocation-free after warm-up.</summary>
    public void Step(float dt)
    {
        AssertOwner();
        long start = Stopwatch.GetTimestamp();

        if (_staticsSinceOptimize >= OptimizeAfterStatics || (_staticsSinceOptimize > 0 && !_optimizedOnce))
        {
            _system.OptimizeBroadPhase();
            _staticsSinceOptimize = 0;
            _optimizedOnce = true;
        }

        int collisionSteps = Math.Max(1, (int)MathF.Ceiling(dt / CollisionStepDt - 1e-3f));
        LastStepError = _system.Update(dt, collisionSteps, _tempAllocator, _jobSystem);
        LastStepMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    /// <summary>Adds a static (NonMoving layer) body using <paramref name="shape"/>. The body takes its own Jolt reference.</summary>
    public BodyHandle AddStatic(StaticMeshShape shape, Vector3 position)
    {
        AssertOwner();

        using var settings = BodyCreationSettings.Create(shape.JoltShape, ToJolt(position), floatQuat.Identity, MotionType.Static, ObjectLayers.NonMoving);
        uint id = CreateAndAdd(settings, Activation.DontActivate);
        Track(id, BodyKind.StaticMesh, position, shape.Bvh);
        _staticsSinceOptimize++;
        return BodyHandle.FromJolt(id);
    }

    public BodyHandle AddDynamicSphere(Vector3 position, float radius, float density = 1000f)
    {
        AssertOwner();

        using var shapeSettings = SphereShapeSettings.Create(radius);
        shapeSettings.SetDensity(density);
        return AddDynamic(shapeSettings, position, BodyKind.Sphere, new Vector3(radius));
    }

    public BodyHandle AddDynamicBox(Vector3 position, Vector3 halfExtents, float density = 1000f)
    {
        AssertOwner();

        float convexRadius = MathF.Min(MaxBoxConvexRadius, MathF.Min(halfExtents.X, MathF.Min(halfExtents.Y, halfExtents.Z)));
        using var shapeSettings = BoxShapeSettings.Create(ToJolt(halfExtents), convexRadius);
        shapeSettings.SetDensity(density);
        return AddDynamic(shapeSettings, position, BodyKind.Box, halfExtents);
    }

    BodyHandle AddDynamic(ShapeSettings shapeSettings, Vector3 position, BodyKind kind, Vector3 shapeData)
    {
        using var shape = shapeSettings.CreateShape();
        using var settings = BodyCreationSettings.Create(shape, ToJolt(position), floatQuat.Identity, MotionType.Dynamic, ObjectLayers.Moving);
        uint id = CreateAndAdd(settings, Activation.Activate);
        Track(id, kind, shapeData, null);
        return BodyHandle.FromJolt(id);
    }

    uint CreateAndAdd(BodyCreationSettings settings, Activation activation)
    {
        uint id = _bodies.CreateAndAddBody(settings, activation);

        if (id == InvalidJoltId)
            throw new InvalidOperationException("Jolt body limit reached.");

        return id;
    }

    void Track(uint id, BodyKind kind, Vector3 shapeData, TriangleBvh? bvh)
    {
        int index = (int)(id & BodyIndexMask);
        _joltIds[index] = id;
        _kinds[index] = kind;
        _shapeData[index] = shapeData;
        _bvhs[index] = bvh;

        if (kind == BodyKind.StaticMesh)
        {
            _denseSlots[index] = _staticCount;
            _staticMin[_staticCount] = bvh is null ? default : bvh.Min + shapeData;
            _staticMax[_staticCount] = bvh is null ? default : bvh.Max + shapeData;
            _staticIds[_staticCount++] = id;
        }
        else
        {
            _dynamicBodies[index] = _bodyLocks.TryGetBody(id);
            _denseSlots[index] = _dynamicCount;
            _dynamicIds[_dynamicCount++] = id;
        }
    }

    /// <summary>Removes and destroys the body. Unknown or stale handles are ignored.</summary>
    public void Remove(BodyHandle body)
    {
        AssertOwner();

        if (!TryGetIndex(body, out int index))
            return;

        uint id = body.JoltId;
        int slot = _denseSlots[index];

        if (_kinds[index] == BodyKind.StaticMesh)
        {
            _staticMin[slot] = _staticMin[_staticCount - 1];
            _staticMax[slot] = _staticMax[_staticCount - 1];
            RemoveDense(_staticIds, ref _staticCount, slot);
        }
        else
        {
            RemoveDense(_dynamicIds, ref _dynamicCount, slot);
        }

        _kinds[index] = BodyKind.None;
        _bvhs[index] = null;
        _dynamicBodies[index] = default;
        _joltIds[index] = InvalidJoltId;
        _bodies.RemoveBody(id);
        _bodies.DestroyBody(id);
    }

    void RemoveDense(uint[] ids, ref int count, int slot)
    {
        uint last = ids[--count];
        ids[slot] = last;
        _denseSlots[last & BodyIndexMask] = slot;
    }

    bool TryGetIndex(BodyHandle body, out int index)
    {
        index = (int)(body.JoltId & BodyIndexMask);
        return body.IsValid && index < _kinds.Length && _kinds[index] != BodyKind.None && _joltIds[index] == body.JoltId;
    }

    public Vector3 GetPosition(BodyHandle body) => ToNumerics(_bodies.GetPosition(body.JoltId));

    public Quaternion GetRotation(BodyHandle body) => ToNumerics(_bodies.GetRotation(body.JoltId));

    public Vector3 GetLinearVelocity(BodyHandle body) => ToNumerics(_bodies.GetLinearVelocity(body.JoltId));

    public Vector3 GetAngularVelocity(BodyHandle body) => ToNumerics(_bodies.GetAngularVelocity(body.JoltId));

    public Vector3 GetCenterOfMass(BodyHandle body) => ToNumerics(_bodies.GetCenterOfMassPosition(body.JoltId));

    /// <summary>Force in N at the centre of mass, applied over the next step. Wakes the body.</summary>
    public void AddForce(BodyHandle body, Vector3 force) => _bodies.AddForce(body.JoltId, ToJolt(force));

    /// <summary>Force in N at a world-space point, applied over the next step. Wakes the body.</summary>
    public void AddForceAtPoint(BodyHandle body, Vector3 force, Vector3 point) => _bodies.AddForce(body.JoltId, ToJolt(force), ToJolt(point));

    /// <summary>
    /// Closest hit along the ray. Static mesh normals follow triangle winding (counter-clockwise = front) and meshes are
    /// hit from both sides. Uses Jolt's NarrowPhaseQuery when the native library exports it; otherwise a managed path
    /// (linear scan of contiguous static AABBs + per-mesh BVH + analytic sphere/box), see <see cref="JoltRuntime"/>.
    /// </summary>
    public RayHit CastRay(Vector3 origin, Vector3 direction, float maxDistance)
    {
        AssertOwner();
        float lengthSq = direction.LengthSquared();

        if (lengthSq <= 0f || !(maxDistance > 0f))
            return default;

        var dir = direction / MathF.Sqrt(lengthSq);

        if (JoltRuntime.NativeRayCast)
            return CastRayNative(origin, dir, maxDistance);

        var invDir = TriangleBvh.InverseDirection(dir);
        float best = maxDistance;
        uint hitId = InvalidJoltId;
        Vector3 hitNormal = default;

        for (int i = 0; i < _staticCount; i++)
        {
            if (!TriangleBvh.RayAabb(origin, invDir, _staticMin[i], _staticMax[i], best))
                continue;

            uint id = _staticIds[i];
            int index = (int)(id & BodyIndexMask);
            var bvh = _bvhs[index]!;
            var localOrigin = origin - _shapeData[index];

            if (bvh.CastRay(localOrigin, dir, invDir, ref best, out var normal))
            {
                hitId = id;
                hitNormal = normal;
            }
        }

        for (int i = 0; i < _dynamicCount; i++)
        {
            uint id = _dynamicIds[i];
            int index = (int)(id & BodyIndexMask);
            var body = _dynamicBodies[index];
            var center = ToNumerics(body.GetPosition());
            var extents = _shapeData[index];
            Vector3 normal;

            if (_kinds[index] == BodyKind.Sphere)
            {
                if (!RaySphere(origin, dir, center, extents.X, ref best, out normal))
                    continue;
            }
            else
            {
                float bound = best;

                if (!RaySphere(origin, dir, center, extents.Length(), ref bound, out _))
                    continue;

                if (!RayBox(origin, dir, center, ToNumerics(body.GetRotation()), extents, ref best, out normal))
                    continue;
            }

            hitId = id;
            hitNormal = normal;
        }

        if (hitId == InvalidJoltId)
            return default;

        return new RayHit(origin + dir * best, hitNormal, best, BodyHandle.FromJolt(hitId));
    }

    RayHit CastRayNative(Vector3 origin, Vector3 dir, float maxDistance)
    {
        var ray = new RRayCast { Origin = ToJolt(origin), Direction = ToJolt(dir * maxDistance) };
        var result = new RayCastResult { BodyID = InvalidJoltId, Fraction = RayFractionMiss };

        if (!_narrowPhase.CastRay(in ray, ref result))
            return default;

        float distance = result.Fraction * maxDistance;
        var point = origin + dir * distance;
        var body = _bodyLocks.TryGetBody(result.BodyID);
        var normal = body.IsNull() ? -dir : ToNumerics(body.GetWorldSpaceSurfaceNormal(result.SubShapeID2, ToJolt(point)));
        return new RayHit(point, normal, distance, BodyHandle.FromJolt(result.BodyID));
    }

    static bool RaySphere(Vector3 origin, Vector3 dir, Vector3 center, float radius, ref float best, out Vector3 normal)
    {
        var oc = origin - center;
        float b = Vector3.Dot(oc, dir);
        float c = oc.LengthSquared() - radius * radius;
        normal = default;

        if (c > 0f && b > 0f)
            return false;

        float disc = b * b - c;

        if (disc < 0f)
            return false;

        float t = MathF.Max(0f, -b - MathF.Sqrt(disc));

        if (t >= best)
            return false;

        best = t;
        normal = c > 0f ? Vector3.Normalize(origin + dir * t - center) : -dir;
        return true;
    }

    static bool RayBox(Vector3 origin, Vector3 dir, Vector3 center, Quaternion rotation, Vector3 halfExtents, ref float best, out Vector3 normal)
    {
        var inverse = Quaternion.Conjugate(rotation);
        var lo = Vector3.Transform(origin - center, inverse);
        var ld = Vector3.Transform(dir, inverse);
        var inv = TriangleBvh.InverseDirection(ld);
        var t0 = (-halfExtents - lo) * inv;
        var t1 = (halfExtents - lo) * inv;
        var tNear = Vector3.Min(t0, t1);
        var tFar = Vector3.Max(t0, t1);
        float tExit = MathF.Min(tFar.X, MathF.Min(tFar.Y, tFar.Z));
        int axis = tNear.X >= tNear.Y && tNear.X >= tNear.Z ? 0 : tNear.Y >= tNear.Z ? 1 : 2;
        float tEnter = tNear[axis];
        normal = default;

        if (tEnter > tExit || tExit < 0f)
            return false;

        if (tEnter <= 0f)
        {
            if (best <= 0f)
                return false;

            best = 0f;
            normal = -dir;
            return true;
        }

        if (tEnter >= best)
            return false;

        var localNormal = Vector3.Zero;
        localNormal[axis] = ld[axis] > 0f ? -1f : 1f;
        best = tEnter;
        normal = Vector3.Transform(localNormal, rotation);
        return true;
    }

    [Conditional("DEBUG")]
    void AssertOwner() => Debug.Assert(Environment.CurrentManagedThreadId == _ownerThread, "PhysicsWorld is main-thread only.");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float3 ToJolt(Vector3 v) => Unsafe.BitCast<Vector3, float3>(v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Vector3 ToNumerics(float3 v) => Unsafe.BitCast<float3, Vector3>(v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Quaternion ToNumerics(floatQuat q) => Unsafe.BitCast<floatQuat, Quaternion>(q);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DestroyAll(_staticIds.AsSpan(0, _staticCount));
        DestroyAll(_dynamicIds.AsSpan(0, _dynamicCount));
        _staticCount = 0;
        _dynamicCount = 0;

        _system.Dispose();
        _objectVsBroadPhase.Dispose();
        _layerPairFilter.Dispose();
        _broadPhaseLayers.Dispose();
        _jobPool.Dispose();
        _tempAllocatorImpl.Dispose();
    }

    void DestroyAll(Span<uint> ids)
    {
        if (ids.IsEmpty)
            return;

        _bodies.RemoveBodies(ids);
        _bodies.DestroyBodies(ids);
    }
}
