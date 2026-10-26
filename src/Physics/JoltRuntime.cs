using System.Runtime.InteropServices;
using Brutal.JoltApi;

namespace BrutalGame.Physics;

/// <summary>
/// Process-wide Jolt registration (allocator, factory, types). Done once, lazily, on first use from any thread.
/// Never torn down: shapes may outlive worlds, and the OS reclaims it at exit.
/// <para>
/// Also probes the native library: Ahwoo.BRUTAL.Jolt 2026.10.2 ships a native jolt that lacks ~980 of the managed
/// entry points, including indexed MeshShapeSettings construction and every NarrowPhaseQuery ray cast. When missing,
/// static meshes fall back to a StaticCompoundShape of TriangleShapes and ray casts to a managed BVH.
/// </para>
/// </summary>
internal static class JoltRuntime
{
    static readonly Lock Gate = new();
    static volatile bool _initialized;

    public static bool NativeMeshShape { get; private set; }
    public static bool NativeRayCast { get; private set; }

    public static void EnsureInitialized()
    {
        if (_initialized)
            return;

        lock (Gate)
        {
            if (_initialized)
                return;

            Jolt.RegisterDefaultAllocator();
            Factory.Instance = Factory.Create();
            Jolt.RegisterTypes();

            var lib = NativeLibrary.Load("jolt", typeof(Jolt).Assembly, null);
            NativeMeshShape = NativeLibrary.TryGetExport(lib, "MeshShapeSettings_Create_3", out _);
            NativeRayCast = NativeLibrary.TryGetExport(lib, "NarrowPhaseQuery_CastRay", out _);
            _initialized = true;
        }
    }
}
