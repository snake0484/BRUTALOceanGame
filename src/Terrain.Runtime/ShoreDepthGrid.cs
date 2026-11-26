using System.Diagnostics;
using System.Numerics;
using Brutal.Concurrency.Jobs;
using Brutal.Numerics;
using BrutalGame.Engine.Jobs;

namespace BrutalGame.Terrain.Runtime;

/// <summary>
/// The ocean's depth maps round the camera (planets.md "Ocean and shore wash on a sphere"): radial depth below the sea
/// sphere, sea level − ground, negative on land, sampled in a tangent frame on the sea sphere (the client's sea frame:
/// x east, z south, bent to the sphere by (x² + z²) / 2R). A GPU cache of <see cref="PlanetShore"/>, which gameplay reads
/// directly.
/// <para>
/// Near: <see cref="Resolution"/>² cells of <see cref="CellSize"/> (1 km) that follow the camera, generated ground plus
/// edits; the shore wash reads it. Stored toroidally (cell g at texel g mod Resolution), so moving the window only fills
/// the strips that came in, on jobs; until they land the shader reads those cells from the far map
/// (<see cref="NearValid"/>), so the main thread does no per-cell work.
/// Far: <see cref="FarResolution"/>² cells of <see cref="FarCellSize"/> (24.6 km) centred on the frame's origin, the
/// generated ground at coarse detail, baked once per frame: the swell's depth cap beyond the near window.
/// </para>
/// A new frame is baked in the background (<see cref="Prepare"/>) while the current one stays in use, then swapped in
/// whole (<see cref="Swap"/>). Main thread, except the jobs.
/// </summary>
public sealed class ShoreDepthGrid : IDisposable
{
    public const float CellSize = 2f;
    public const int Resolution = 512;
    public const float FarCellSize = 48f;
    public const int FarResolution = 512;

    /// <summary>Depth read outside both maps: open water.</summary>
    public const float DeepDepth = 50f;

    /// <summary>Dry ground stores at least this (m): anything higher above the sea never meets the surface.</summary>
    public const float MinDepth = -20f;
    public const float MaxDepth = 1000f;

    /// <summary>Octaves the far map evaluates: 32 m and longer (LOD 4).</summary>
    public const int FarLod = 4;

    private const int SnapCells = 16;
    private const int DriftCells = Resolution / 8;
    private const int BandRows = 64;
    private const int Bands = Resolution / BandRows + FarResolution / BandRows;
    private const int MaxStrips = 24;
    private const int MaxDirty = 16;
    private const int JobCells = Resolution * BandRows;

    /// <summary>Cell rectangle [X0, X1) × [Z0, Z1): global cells for the near map, texels for uploads.</summary>
    public readonly record struct Rect(int X0, int Z0, int X1, int Z1)
    {
        public bool Empty => X1 <= X0 || Z1 <= Z0;

        public int Area => Empty ? 0 : (X1 - X0) * (Z1 - Z0);

        public Rect Clip(in Rect o) => new(Math.Max(X0, o.X0), Math.Max(Z0, o.Z0), Math.Min(X1, o.X1), Math.Min(Z1, o.Z1));
    }

    /// <summary>The tangent frame the maps are sampled in.</summary>
    public readonly record struct Frame(double3 Origin, double3 X, double3 Up, double3 Z, double SeaRadius)
    {
        public double3 Direction(double x, double z) =>
            double3.Normalize(Origin + X * x + Z * z - Up * ((x * x + z * z) / (2.0 * SeaRadius)));

        public double3 ToLocal(double3 planet)
        {
            var d = planet - Origin;
            return new double3(double3.Dot(d, X), double3.Dot(d, Up), double3.Dot(d, Z));
        }
    }

    /// <summary>One frame's maps.</summary>
    private sealed class Layer
    {
        public readonly Half[] Near = new Half[Resolution * Resolution];
        public readonly Half[] Far = new Half[FarResolution * FarResolution];
        public Frame Frame;
        public int Generation;
        public int OriginX;
        public int OriginZ;
        public bool NearOn;
        public bool Maps;

        /// <summary>Bands of the first fill still to come (far map, near window), and strips of the moving window.</summary>
        public int FarLeft;
        public int NearLeft;
        public int StripsLeft;
        public float FarMs;

        /// <summary>The cells of the window that hold their depth (global cells).</summary>
        public Rect Valid;

        public Rect Window => new(OriginX, OriginZ, OriginX + Resolution, OriginZ + Resolution);
    }

    private readonly record struct Request(Layer Layer, int Generation, Rect Region, bool Far, bool First);

    private sealed class DepthJob(PlanetShore shore) : IJob
    {
        public readonly Half[] Cells = new Half[JobCells];
        public Request Request;
        public Frame Frame;
        public float Ms;

        public void Run()
        {
            var start = Stopwatch.GetTimestamp();
            var region = Request.Region;
            var far = Request.Far;
            var width = region.X1 - region.X0;
            var cell = far ? FarCellSize : CellSize;
            var offset = far ? -0.5 * FarResolution * FarCellSize : 0.0;

            for (var z = region.Z0; z < region.Z1; z++)
            {
                var row = (z - region.Z0) * width - region.X0;
                var pz = offset + (z + 0.5) * cell;

                for (var x = region.X0; x < region.X1; x++)
                {
                    var d = Frame.Direction(offset + (x + 0.5) * cell, pz);
                    var depth = far ? shore.GeneratedDepth(d.X, d.Y, d.Z, FarLod) : shore.Depth(d.X, d.Y, d.Z);
                    Cells[row + x] = (Half)Math.Clamp(depth, MinDepth, MaxDepth);
                }
            }

            Ms = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
    }

    private readonly JobScheduler _scheduler;
    private readonly DepthJob[] _jobs = new DepthJob[Bands + MaxStrips];
    private readonly List<Request> _requests = new(2 * Bands + MaxStrips);
    private readonly Rect[] _nearDirty = new Rect[MaxDirty];
    private readonly Rect[] _farDirty = new Rect[MaxDirty];
    private Layer _current = new();
    private Layer _next = new();
    private int _running;
    private int _nearDirtyCount;
    private int _farDirtyCount;
    private bool _preparing;
    private int _generation;

    public ShoreDepthGrid(PlanetShore shore)
    {
        _scheduler = new JobScheduler((uint)WorkerBudget.Side, "ShoreDepth", ThreadPriority.BelowNormal);

        for (var i = 0; i < _jobs.Length; i++)
            _jobs[i] = new DepthJob(shore);

        Array.Fill(_current.Near, (Half)DeepDepth);
        Array.Fill(_current.Far, (Half)DeepDepth);
    }

    /// <summary>The frame in use has its maps (false: every lookup reads open water).</summary>
    public bool HasMaps => _current.Maps;

    /// <summary>Nothing is being baked or waiting: the maps hold what <see cref="PlanetShore"/> says.</summary>
    public bool Settled => _running == 0 && _requests.Count == 0;

    /// <summary>A frame given to <see cref="Prepare"/> is baked and waits for <see cref="Swap"/>.</summary>
    public bool NextReady => _preparing && _next.FarLeft == 0 && _next.NearLeft == 0;

    public bool Preparing => _preparing;

    /// <summary>Worker time (ms) and cells of the last finished job, and the jobs run so far.</summary>
    public float LastJobMs { get; private set; }

    public int LastJobCells { get; private set; }

    public int Jobs { get; private set; }

    /// <summary>The last far bake's summed worker time (ms).</summary>
    public float LastFarBakeMs { get; private set; }

    public ReadOnlySpan<Half> Near => _current.Near;

    public ReadOnlySpan<Half> Far => _current.Far;

    public ReadOnlySpan<Rect> NearDirty => _nearDirty.AsSpan(0, _nearDirtyCount);

    public ReadOnlySpan<Rect> FarDirty => _farDirty.AsSpan(0, _farDirtyCount);

    /// <summary>Global cell of the near window's corner.</summary>
    public (int X, int Z) Origin => (_current.OriginX, _current.OriginZ);

    /// <summary>Counts every <see cref="Swap"/>: cells of different frames are unrelated.</summary>
    public int FrameVersion { get; private set; }

    public void ClearDirty() => (_nearDirtyCount, _farDirtyCount) = (0, 0);

    /// <summary>
    /// (window corner x, z relative to the camera, world size, depth outside the maps) for shaders; size 0 while the
    /// near window is off.
    /// </summary>
    public Vector4 NearRect(double cameraX, double cameraZ)
    {
        if (!_current.Maps || !_current.NearOn)
            return new Vector4(0f, 0f, 0f, DeepDepth);

        return new Vector4((float)(_current.OriginX * (double)CellSize - cameraX), (float)(_current.OriginZ * (double)CellSize - cameraZ), Resolution * CellSize, DeepDepth);
    }

    /// <summary>
    /// The part of the near window that holds its depth, (min x, min z, max x, max z) relative to the camera; empty
    /// (min above max) while there is none. Outside it the far map stands in.
    /// </summary>
    public Vector4 NearValid(double cameraX, double cameraZ)
    {
        var valid = _current.Valid;

        if (!_current.Maps || !_current.NearOn || valid.Empty)
            return new Vector4(1f, 1f, 0f, 0f);

        return new Vector4(
            (float)(valid.X0 * (double)CellSize - cameraX), (float)(valid.Z0 * (double)CellSize - cameraZ),
            (float)(valid.X1 * (double)CellSize - cameraX), (float)(valid.Z1 * (double)CellSize - cameraZ));
    }

    /// <summary>(corner x, z relative to the camera, world size, 0) of the far map; size 0 without maps.</summary>
    public Vector4 FarRect(double cameraX, double cameraZ)
    {
        if (!_current.Maps)
            return default;

        const float size = FarResolution * FarCellSize;
        return new Vector4((float)(-0.5 * size - cameraX), (float)(-0.5 * size - cameraZ), size, 0f);
    }

    /// <summary>
    /// Starts baking the maps of a new frame with the camera at (x, z) in it; the frame in use is untouched until
    /// <see cref="Swap"/>. With <paramref name="maps"/> false the frame has none (high up: the coast is the terrain's) and
    /// is ready at once. Replaces a frame still being prepared.
    /// </summary>
    public void Prepare(in Frame frame, double cameraX, double cameraZ, bool maps, bool near)
    {
        var layer = _next;
        Forget(layer);
        layer.Frame = frame;
        layer.Generation = ++_generation;
        layer.Maps = maps;
        layer.NearOn = maps && near;
        layer.FarLeft = 0;
        layer.NearLeft = 0;
        layer.FarMs = 0f;
        _preparing = true;

        if (!maps)
            return;

        for (var z = 0; z < FarResolution; z += BandRows)
        {
            _requests.Add(new Request(layer, layer.Generation, new Rect(0, z, FarResolution, z + BandRows), true, true));
            layer.FarLeft++;
        }

        if (layer.NearOn)
            StartWindow(layer, cameraX, cameraZ);

        Pump();
    }

    /// <summary>Makes the prepared frame the one in use; everything is uploaded again.</summary>
    public void Swap()
    {
        Forget(_current);
        (_current, _next) = (_next, _current);
        _preparing = false;
        FrameVersion++;
        _nearDirtyCount = 0;
        _farDirtyCount = 0;
        MarkDirty(_nearDirty, ref _nearDirtyCount, new Rect(0, 0, Resolution, Resolution), Resolution);
        MarkDirty(_farDirty, ref _farDirtyCount, new Rect(0, 0, FarResolution, FarResolution), FarResolution);
    }

    /// <summary>
    /// Per frame: takes finished jobs, moves the near window with the camera (at (x, z) of the frame in use) and queues
    /// what it exposed. <paramref name="near"/> turns the near window on or off (off from altitude, where nothing reads it).
    /// </summary>
    public void Update(double cameraX, double cameraZ, bool near)
    {
        var layer = _current;

        if (layer.Maps && layer.FarLeft == 0)
        {
            if (layer.NearOn != near)
            {
                layer.NearOn = near;
                Forget(layer);

                if (near)
                    StartWindow(layer, cameraX, cameraZ);
            }
            else if (near)
            {
                Recentre(layer, cameraX, cameraZ);
            }
        }

        Pump();
    }

    /// <summary>An accepted edit: the near cells under it are evaluated again (it is already in <see cref="PlanetShore"/>).</summary>
    public void MarkEdit(in EditOp op)
    {
        var layer = _current;

        if (!layer.Maps || !layer.NearOn)
            return;

        op.Centre(out var x, out var y, out var z);
        var local = layer.Frame.ToLocal(new double3(x, y, z));
        var reach = op.Radius + 2.0 * CellSize;
        var region = new Rect(
            (int)Math.Floor((local.X - reach) / CellSize), (int)Math.Floor((local.Z - reach) / CellSize),
            (int)Math.Ceiling((local.X + reach) / CellSize), (int)Math.Ceiling((local.Z + reach) / CellSize)).Clip(layer.Window);

        Strip(layer, region);
    }

    /// <summary>Drops the layer's waiting requests; its jobs in flight are ignored when they land (a new generation).</summary>
    private void Forget(Layer layer)
    {
        for (var i = _requests.Count - 1; i >= 0; i--)
        {
            if (_requests[i].Layer == layer)
                _requests.RemoveAt(i);
        }

        layer.Generation = ++_generation;
        layer.NearLeft = 0;
        layer.StripsLeft = 0;
    }

    /// <summary>The near window centred on the camera, nothing in it valid yet: queues its bands.</summary>
    private void StartWindow(Layer layer, double cameraX, double cameraZ)
    {
        layer.OriginX = Snap(cameraX);
        layer.OriginZ = Snap(cameraZ);
        layer.NearLeft = 0;
        layer.Valid = default;

        for (var z = 0; z < Resolution; z += BandRows)
        {
            var region = new Rect(layer.OriginX, layer.OriginZ + z, layer.OriginX + Resolution, layer.OriginZ + z + BandRows);
            _requests.Add(new Request(layer, layer.Generation, region, false, true));
            layer.NearLeft++;
        }
    }

    private static int Snap(double camera) => (int)Math.Floor(camera / (CellSize * SnapCells)) * SnapCells - Resolution / 2;

    private void Recentre(Layer layer, double cameraX, double cameraZ)
    {
        const int half = Resolution / 2;
        var cx = cameraX / CellSize - layer.OriginX - half;
        var cz = cameraZ / CellSize - layer.OriginZ - half;

        if (Math.Abs(cx) < DriftCells && Math.Abs(cz) < DriftCells)
            return;

        var old = layer.Window;
        layer.OriginX = Snap(cameraX);
        layer.OriginZ = Snap(cameraZ);
        var window = layer.Window;
        var kept = old.Clip(window);

        if (kept.Empty)
        {
            Forget(layer);
            StartWindow(layer, cameraX, cameraZ);
            return;
        }

        // What stays valid moved with the window; what came in (the rows above and below the old window, then the
        // columns beside what is left) is read from the far map until its strips land.
        layer.Valid = layer.Valid.Clip(window);
        Strip(layer, new Rect(window.X0, window.Z0, window.X1, kept.Z0));
        Strip(layer, new Rect(window.X0, kept.Z1, window.X1, window.Z1));
        Strip(layer, new Rect(window.X0, kept.Z0, kept.X0, kept.Z1));
        Strip(layer, new Rect(kept.X1, kept.Z0, window.X1, kept.Z1));
    }

    /// <summary>Queues near cells for the exact depth, in bands a job's buffer holds.</summary>
    private void Strip(Layer layer, Rect region)
    {
        if (region.Empty)
            return;

        // Too far behind (fast flight): start the window's fill over; the far map's depth holds meanwhile.
        if (_requests.Count + (region.Z1 - region.Z0 + BandRows - 1) / BandRows > MaxStrips)
        {
            Forget(layer);
            region = layer.Window;
        }

        for (var z = region.Z0; z < region.Z1; z += BandRows)
        {
            _requests.Add(new Request(layer, layer.Generation, new Rect(region.X0, z, region.X1, Math.Min(z + BandRows, region.Z1)), false, false));
            layer.StripsLeft++;
        }
    }

    /// <summary>Once the last batch has finished: stores its results and starts the next from the waiting requests.</summary>
    private void Pump()
    {
        if (_scheduler.IsExecuting)
            return;

        for (var i = 0; i < _running; i++)
            Collect(_jobs[i]);

        _running = 0;
        var taken = 0;

        while (taken < _requests.Count && _running < _jobs.Length)
        {
            var request = _requests[taken++];

            if (request.Generation != request.Layer.Generation)
                continue;

            var job = _jobs[_running++];
            job.Request = request;
            job.Frame = request.Layer.Frame;
            _scheduler.Queue(job, 1);
        }

        _requests.RemoveRange(0, taken);

        if (_running > 0)
            _scheduler.ExecuteJobs();
    }

    private void Collect(DepthJob job)
    {
        var request = job.Request;
        var layer = request.Layer;
        Jobs++;
        LastJobMs = job.Ms;
        LastJobCells = request.Region.Area;

        if (request.Generation != layer.Generation)
            return;

        if (request.Far)
        {
            job.Cells.AsSpan(0, request.Region.Area).CopyTo(layer.Far.AsSpan(request.Region.Z0 * FarResolution));
            layer.FarMs += job.Ms;

            if (--layer.FarLeft == 0)
                LastFarBakeMs = layer.FarMs;

            return;
        }

        if (request.First)
            layer.NearLeft--;
        else
            layer.StripsLeft--;

        if (layer.NearLeft == 0 && layer.StripsLeft == 0)
            layer.Valid = layer.Window;

        var region = request.Region.Clip(layer.Window);

        if (region.Empty)
            return;

        var width = request.Region.X1 - request.Region.X0;

        for (var z = region.Z0; z < region.Z1; z++)
        {
            var source = (z - request.Region.Z0) * width - request.Region.X0;
            var row = Wrap(z) * Resolution;

            for (var x = region.X0; x < region.X1; x++)
                layer.Near[row + Wrap(x)] = job.Cells[source + x];
        }

        if (layer == _current)
            MarkNearDirty(region);
    }

    private static int Wrap(int cell) => cell & (Resolution - 1);

    /// <summary>A rectangle of global cells as texel rectangles: it wraps at the texture's edges into up to four.</summary>
    private void MarkNearDirty(in Rect region)
    {
        var x0 = Wrap(region.X0);
        var z0 = Wrap(region.Z0);
        var w = Math.Min(region.X1 - region.X0, Resolution);
        var h = Math.Min(region.Z1 - region.Z0, Resolution);
        var w0 = Math.Min(w, Resolution - x0);
        var h0 = Math.Min(h, Resolution - z0);

        MarkDirty(_nearDirty, ref _nearDirtyCount, new Rect(x0, z0, x0 + w0, z0 + h0), Resolution);
        MarkDirty(_nearDirty, ref _nearDirtyCount, new Rect(0, z0, w - w0, z0 + h0), Resolution);
        MarkDirty(_nearDirty, ref _nearDirtyCount, new Rect(x0, 0, x0 + w0, h - h0), Resolution);
        MarkDirty(_nearDirty, ref _nearDirtyCount, new Rect(0, 0, w - w0, h - h0), Resolution);
    }

    private static void MarkDirty(Rect[] dirty, ref int count, in Rect region, int resolution)
    {
        if (region.Empty)
            return;

        if (count < dirty.Length)
        {
            dirty[count++] = region;
            return;
        }

        dirty[0] = new Rect(0, 0, resolution, resolution);
        count = 1;
    }

    public void Dispose() => _scheduler.AbortJobs(waitForCompletion: true);
}
