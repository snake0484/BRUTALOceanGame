using System.Diagnostics;
using Brutal.Concurrency.Jobs;
using Brutal.Numerics;
using BrutalGame.Water.Ocean;

namespace BrutalGame.Water.Coast;

public enum CoastRegionState : byte
{
    Free,
    Baking,
    Live,
    Retiring,
}

/// <summary>One slot of a <see cref="CoastRegionSet"/>.</summary>
public struct CoastRegion
{
    public CoastRegionKey Key;
    public CoastRegionState State;

    /// <summary>0..1 along the slot's fade in (live) or out (retiring); drawn with <see cref="Weight"/>.</summary>
    public float Fade;

    /// <summary>Counts the slot's bakes: the atlas holds another map when it changes.</summary>
    public int Version;

    /// <summary>The order the slots went live in: of two maps of one window, the lower is the one fading out.</summary>
    public int Sequence;
    public CoastWindow Window;

    public readonly bool Drawn => State is CoastRegionState.Live or CoastRegionState.Retiring && Fade > 0f;

    /// <summary>Smooth in the fade, and complementary: a map fading in and the one it replaces sum to 1.</summary>
    public readonly float Weight => Fade * Fade * (3f - 2f * Fade);
}

/// <summary>
/// The coast regions round the camera (breaking-waves-brief.md §3): the nearest <see cref="MaxDesired"/> lattice windows
/// that hold a coast are baked for the swell's carrier, one at a time on a job, into a fixed pool of
/// <see cref="Capacity"/> atlases. Windows load within <see cref="LoadDistance"/> of the camera and unload past
/// <see cref="UnloadDistance"/>, fading in and out over <see cref="FadeSeconds"/>; when the carrier leaves the baked one's
/// tolerance each window is baked again into the spare slot and cross-faded, and inside it only the drawn height
/// follows. Whether a window holds a coast comes from a probe of its depths, remembered in a fixed table. The main thread only coordinates; nothing is allocated after construction.
/// </summary>
public sealed class CoastRegionSet : IDisposable
{
    public const int Capacity = 8;
    public const int MaxDesired = Capacity - 1;
    public const double LoadDistance = 3000.0;
    public const double UnloadDistance = 6000.0;
    public const float FadeSeconds = 2f;

    /// <summary>The maps are kept while the swell's carrier is within these of the one they were baked for: 1° of direction, and shares of its frequency and height.</summary>
    public const int DirectionTolerance = CoastCarrier.DirectionSteps / 360;
    public const float OmegaTolerance = 0.01f;
    public const float HeightTolerance = 0.05f;

    /// <summary>A kept map's drawn height follows the carrier's over this (s).</summary>
    private const float HeightSeconds = 2f;

    /// <summary>A loaded window ranks as this much nearer (m), so two windows at the pool's edge do not swap back and forth.</summary>
    private const double Stickiness = 500.0;

    /// <summary>The windows wanted are chosen again once the camera has moved this far (m), or something changed.</summary>
    private const double Reselect = 32.0;

    /// <summary>A window's centre this much past a distance in its tile's plane (m) is past it on the sphere too, within the tiles' reach.</summary>
    private const double PlaneSlack = 64.0;
    private const int ProbeBatch = 16;
    private const int ProbeSlots = 512;
    private const int ProbeReach = 8;
    private const int MaxTiles = SeaTiles.MaxBlend;

    private enum Work
    {
        None,
        Probe,
        Bake,
    }

    private struct ProbeEntry
    {
        public int Tile;
        public short X;
        public short Z;
        public bool Known;
        public float Min;
        public float Max;
        public long Stamp;
    }

    private struct Candidate
    {
        public int Tile;
        public short X;
        public short Z;
        public double Rank;
    }

    private sealed class ProbeJob(ICoastDepth depth) : IJob
    {
        public readonly CoastWindow[] Windows = new CoastWindow[ProbeBatch];
        public readonly Candidate[] Keys = new Candidate[ProbeBatch];
        public readonly float[] Min = new float[ProbeBatch];
        public readonly float[] Max = new float[ProbeBatch];
        public int Count;

        public void Run()
        {
            for (var i = 0; i < Count; i++)
                CoastBake.Probe(depth, Windows[i], out Min[i], out Max[i]);
        }
    }

    private readonly SeaTiles _tiles;
    private readonly ICoastDepth _depth;
    private readonly JobScheduler _scheduler;
    private readonly CoastRegion[] _regions = new CoastRegion[Capacity];
    private readonly CoastAtlas[] _atlases = new CoastAtlas[Capacity];
    private readonly CoastBake _bake = new();
    private readonly ProbeJob _probe;
    private readonly ProbeEntry[] _probes = new ProbeEntry[ProbeSlots];
    private readonly Candidate[] _desired = new Candidate[MaxDesired];
    private int _desiredCount;
    private Work _running;
    private int _bakeSlot;
    private CoastCarrier _carrier;
    private CoastCarrier _baked;
    private float _breakDepth;
    private double3 _selectedAt;
    private bool _dirty = true;
    private long _stamp;
    private int _sequence;

    /// <param name="workers">Runners for the bake: background work nothing waits on.</param>
    public CoastRegionSet(SeaTiles tiles, ICoastDepth depth, int workers)
    {
        _tiles = tiles;
        _depth = depth;
        _scheduler = new JobScheduler((uint)Math.Max(workers, 1), "Coast", ThreadPriority.BelowNormal);
        _probe = new ProbeJob(depth);

        for (var i = 0; i < Capacity; i++)
            _atlases[i] = new CoastAtlas();
    }

    public ReadOnlySpan<CoastRegion> Regions => _regions;

    /// <summary>The carrier the maps are baked for, and the swell's own, which it follows in steps (<see cref="Keeps"/>).</summary>
    public CoastCarrier Carrier => _baked;

    public CoastCarrier Wanted => _carrier;

    /// <summary>Worker time of the last bake (ms) and of its depth sampling; bakes and probes run so far; slots drawn.</summary>
    public float LastBakeMs { get; private set; }

    public float LastBakeDepthMs { get; private set; }

    public int LastBakeDepthSamples { get; private set; }

    public int Bakes { get; private set; }

    public int Probes { get; private set; }

    public int Drawn { get; private set; }

    public bool Busy => _running != Work.None;

    /// <summary>Nothing is baking, waiting to be baked or fading.</summary>
    public bool Settled { get; private set; }

    public static int ResidentBytes => Capacity * CoastAtlas.Bytes;

    public CoastAtlas Atlas(int slot) => _atlases[slot];

    /// <summary>
    /// Per frame, main thread: takes a finished job, moves the fades by <paramref name="seconds"/>, and when the camera
    /// (planet frame) has moved or something changed, chooses the windows wanted again and starts the next job.
    /// </summary>
    /// <param name="significantHeight">Hs of the swell the carrier is of: windows with no water shallower than <see cref="CoastBake.BreakDepth"/>, or than the carrier is drawn in, are left out.</param>
    public void Update(in double3 camera, CoastCarrier carrier, float significantHeight, float seconds)
    {
        var drawn = SurfCarrier.FadeDepth * (1f + HeightTolerance) * MathF.Max(carrier.Height, _baked.Height);
        var breakDepth = MathF.Max(CoastBake.BreakDepth(significantHeight), drawn);

        if (!Keeps(_baked, carrier))
        {
            _baked = carrier;
            _dirty = true;
        }

        if (breakDepth != _breakDepth)
        {
            _breakDepth = breakDepth;
            _dirty = true;
        }

        _carrier = carrier;
        Collect();
        Fade(seconds);
        Heights(seconds);

        if (_dirty || (camera - _selectedAt).LengthSquared() > Reselect * Reselect)
        {
            Select(camera);
            Reconcile();
            _selectedAt = camera;
            _dirty = false;
        }

        if (_running == Work.None)
            Start();

        Settled = _running == Work.None && !Fading();
    }

    /// <summary>A map baked for <paramref name="baked"/> still serves <paramref name="wanted"/>.</summary>
    public static bool Keeps(CoastCarrier baked, CoastCarrier wanted)
    {
        if (baked.Calm || wanted.Calm)
            return baked.Calm && wanted.Calm;

        var turn = Math.Abs(baked.DirectionStep - wanted.DirectionStep);
        turn = Math.Min(turn, CoastCarrier.DirectionSteps - turn);

        return turn <= DirectionTolerance
            && MathF.Abs(wanted.Omega - baked.Omega) <= OmegaTolerance * baked.Omega
            && MathF.Abs(wanted.Height - baked.Height) <= HeightTolerance * baked.Height;
    }

    /// <summary>The height a slot's map is drawn with: the swell's carrier's while the map serves it, else its own.</summary>
    private float DrawHeight(in CoastRegion region) => Keeps(region.Key.Carrier, _carrier) ? _carrier.Height : region.Key.Carrier.Height;

    private void Heights(float seconds)
    {
        var ease = 1f - MathF.Exp(-seconds / HeightSeconds);

        for (var i = 0; i < Capacity; i++)
        {
            ref readonly var region = ref _regions[i];

            if (region.State is not (CoastRegionState.Live or CoastRegionState.Retiring))
                continue;

            var atlas = _atlases[i];
            atlas.Height += (DrawHeight(region) - atlas.Height) * ease;
        }
    }

    private bool Fading()
    {
        for (var i = 0; i < Capacity; i++)
        {
            ref readonly var region = ref _regions[i];

            if (region.State == CoastRegionState.Retiring || (region.State == CoastRegionState.Live && region.Fade < 1f))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The face the loaded regions draw on the sea at unit direction <paramref name="direction"/> at
    /// <paramref name="time"/> (s): each region's <see cref="BreakerFace"/> by its window, fade and tile weight. Vector
    /// parts are in the regions' tile planes, which differ where tiles blend.
    /// </summary>
    public unsafe void Sample(in double3 direction, double time, out FacePoint p)
    {
        p = default;
        _tiles.Locate(direction, out var blend);

        for (var i = 0; i < Capacity; i++)
        {
            ref readonly var region = ref _regions[i];

            if (!region.Drawn)
                continue;

            var tile = 0f;

            for (var k = 0; k < blend.Count; k++)
            {
                if (blend.Tile[k] == region.Key.Tile)
                    tile = blend.Weight[k] * blend.Weight[k];
            }

            if (tile <= 0f)
                continue;

            region.Window.ToLocal(direction, out var x, out var z);
            var weight = tile * region.Weight * CoastWindow.Weight((float)x / CoastWindow.CellSize, (float)z / CoastWindow.CellSize);

            if (weight <= 0f)
                continue;

            var atlas = _atlases[i];
            BreakerFace.Apply(atlas, (float)x, (float)z, atlas.PhaseAt(time), out var face);
            p.Height += weight * face.Height;
            p.SlopeX += weight * face.SlopeX;
            p.SlopeZ += weight * face.SlopeZ;
            p.Dx += weight * face.Dx;
            p.Dz += weight * face.Dz;
            p.DxX += weight * face.DxX;
            p.DzZ += weight * face.DzZ;
            p.DxZ += weight * face.DxZ;
            p.Foam += weight * face.Foam;
        }
    }

    /// <summary>The surf carrier at unit direction <paramref name="direction"/> in the heaviest region drawn there; false where there is none.</summary>
    public bool CarrierAt(in double3 direction, double time, out SurfCarrierPoint point)
    {
        point = default;
        var best = 0f;

        for (var i = 0; i < Capacity; i++)
        {
            ref readonly var region = ref _regions[i];

            if (!region.Drawn)
                continue;

            region.Window.ToLocal(direction, out var x, out var z);
            var weight = region.Weight * CoastWindow.Weight((float)x / CoastWindow.CellSize, (float)z / CoastWindow.CellSize);

            if (weight <= best)
                continue;

            best = weight;
            point = SurfCarrier.At(_atlases[i], (float)x, (float)z, _atlases[i].PhaseAt(time));
        }

        return best > 0f;
    }

    private void Collect()
    {
        if (_running == Work.None || _scheduler.IsExecuting)
            return;

        if (_running == Work.Probe)
        {
            for (var i = 0; i < _probe.Count; i++)
                Remember(_probe.Keys[i].Tile, _probe.Keys[i].X, _probe.Keys[i].Z, _probe.Min[i], _probe.Max[i]);

            Probes += _probe.Count;
            _probe.Count = 0;
        }
        else
        {
            ref var region = ref _regions[_bakeSlot];
            Bakes++;
            LastBakeMs = _bake.Ms;
            LastBakeDepthMs = _bake.DepthMs;
            LastBakeDepthSamples = _bake.DepthSamples;

            // The solve knows which water is the sea's: a window whose shallows are all landlocked is not asked for again.
            if (_bake.Solved)
                Remember(region.Key.Tile, region.Key.X, region.Key.Z, _bake.SeaMin, _bake.ProbeMax);

            if (_bake.Baked && region.Key.Carrier == _baked)
                GoLive(ref region, _atlases[_bakeSlot]);
            else
                region.State = CoastRegionState.Free;
        }

        _running = Work.None;
        _dirty = true;
    }

    /// <summary>The slot's map fades in; any other map of its window fades out under it.</summary>
    private void GoLive(ref CoastRegion region, CoastAtlas atlas)
    {
        atlas.Height = DrawHeight(region);

        for (var i = 0; i < Capacity; i++)
        {
            ref var other = ref _regions[i];

            if (other.State == CoastRegionState.Live && other.Key.SameWindow(region.Key))
                other.State = CoastRegionState.Retiring;
        }

        region.State = CoastRegionState.Live;
        region.Fade = 0f;
        region.Version++;
        region.Sequence = ++_sequence;
    }

    private void Fade(float seconds)
    {
        var step = seconds / FadeSeconds;
        var drawn = 0;

        for (var i = 0; i < Capacity; i++)
        {
            ref var region = ref _regions[i];

            if (region.State == CoastRegionState.Live && region.Fade < 1f)
            {
                region.Fade = MathF.Min(region.Fade + step, 1f);
            }
            else if (region.State == CoastRegionState.Retiring)
            {
                region.Fade -= step;

                if (region.Fade <= 0f)
                {
                    region.Fade = 0f;
                    region.State = CoastRegionState.Free;
                    _dirty = true;
                }
            }

            drawn += region.Drawn ? 1 : 0;
        }

        Drawn = drawn;
    }

    /// <summary>
    /// The nearest coastal windows, nearest first: those loaded while within <see cref="UnloadDistance"/>, and of the
    /// tiles round the camera those within <see cref="LoadDistance"/>; unknown ones are queued for a probe.
    /// </summary>
    private unsafe void Select(in double3 camera)
    {
        _desiredCount = 0;

        if (_running != Work.Probe)
            _probe.Count = 0;

        if (_baked.Calm)
            return;

        for (var i = 0; i < Capacity; i++)
        {
            ref readonly var region = ref _regions[i];

            if (region.State is CoastRegionState.Live or CoastRegionState.Baking && !Desired(region.Key))
                Consider(camera, region.Window, region.Key.Tile, region.Key.X, region.Key.Z, UnloadDistance, Stickiness, loaded: true);
        }

        var dir = double3.Normalize(camera);
        _tiles.Window(dir, out var window);
        const double reach = LoadDistance + PlaneSlack;

        for (var s = 0; s < MaxTiles; s++)
        {
            var tile = window.Tile[s];
            var seen = false;

            for (var b = 0; b < s; b++)
                seen |= window.Tile[b] == tile;

            if (seen)
                continue;

            _tiles.Plane(tile, dir, out var u, out var v);
            var x0 = CoastWindow.Index(u - reach - CoastWindow.Size);
            var x1 = CoastWindow.Index(u + reach) + 1;
            var z0 = CoastWindow.Index(v - reach - CoastWindow.Size);
            var z1 = CoastWindow.Index(v + reach) + 1;

            for (var z = z0; z <= z1; z++)
            {
                var dv = z * CoastWindow.PitchMetres + 0.5 * CoastWindow.Size - v;

                for (var x = x0; x <= x1; x++)
                {
                    var du = x * CoastWindow.PitchMetres + 0.5 * CoastWindow.Size - u;

                    if (du * du + dv * dv > reach * reach || Loaded(tile, (short)x, (short)z))
                        continue;

                    Consider(camera, new CoastWindow(_tiles, tile, x, z), tile, (short)x, (short)z, LoadDistance, 0.0, loaded: false);
                }
            }
        }
    }

    /// <param name="loaded">The window has a map: it holds a coast whatever the probes' table remembers.</param>
    private unsafe void Consider(in double3 camera, in CoastWindow window, int tile, short x, short z, double limit, double stickiness, bool loaded)
    {
        var centre = window.Direction(0.5 * CoastWindow.Size, 0.5 * CoastWindow.Size);
        var distance = (camera - centre * _tiles.SeaRadius).Length();

        if (distance > limit)
            return;

        _tiles.Locate(centre, out var blend);
        var owns = false;

        for (var k = 0; k < blend.Count; k++)
            owns |= blend.Tile[k] == tile;

        if (!owns)
            return;

        var candidate = new Candidate { Tile = tile, X = x, Z = z, Rank = distance - stickiness };

        if (!loaded && !Coastal(candidate, window))
            return;

        var at = _desiredCount;

        while (at > 0 && _desired[at - 1].Rank > candidate.Rank)
            at--;

        if (at >= MaxDesired)
            return;

        _desiredCount = Math.Min(_desiredCount + 1, MaxDesired);

        for (var i = _desiredCount - 1; i > at; i--)
            _desired[i] = _desired[i - 1];

        _desired[at] = candidate;
    }

    /// <summary>The probes' table has the window as a coast; one it does not know is queued for a probe.</summary>
    private bool Coastal(in Candidate candidate, in CoastWindow window)
    {
        var probe = Find(candidate.Tile, candidate.X, candidate.Z);

        if (probe < 0)
        {
            if (_running != Work.Probe && _probe.Count < ProbeBatch)
            {
                _probe.Keys[_probe.Count] = candidate;
                _probe.Windows[_probe.Count++] = window;
            }

            return false;
        }

        _probes[probe].Stamp = ++_stamp;
        return CoastBake.Coastal(_probes[probe].Min, _probes[probe].Max, _breakDepth);
    }

    private bool Loaded(int tile, short x, short z)
    {
        for (var i = 0; i < Capacity; i++)
        {
            ref readonly var region = ref _regions[i];

            if (region.State is CoastRegionState.Live or CoastRegionState.Baking && region.Key.Tile == tile && region.Key.X == x && region.Key.Z == z)
                return true;
        }

        return false;
    }

    private bool Desired(in CoastRegionKey key)
    {
        for (var i = 0; i < _desiredCount; i++)
        {
            if (_desired[i].Tile == key.Tile && _desired[i].X == key.X && _desired[i].Z == key.Z)
                return true;
        }

        return false;
    }

    /// <summary>Live maps of windows no longer wanted fade out; one still fading out when its window is wanted again fades back in.</summary>
    private void Reconcile()
    {
        for (var i = 0; i < Capacity; i++)
        {
            ref var region = ref _regions[i];
            var desired = Desired(region.Key);

            if (region.State == CoastRegionState.Live && !desired)
                region.State = CoastRegionState.Retiring;
            else if (region.State == CoastRegionState.Retiring && desired && region.Key.Carrier == _baked && !Has(region.Key))
                region.State = CoastRegionState.Live;
        }
    }

    /// <summary>Starts the probes waiting, or else the bake of the nearest wanted window that has no map for the carrier.</summary>
    private void Start()
    {
        if (_probe.Count > 0)
        {
            _scheduler.Queue(_probe, 1);
            _scheduler.ExecuteJobs();
            _running = Work.Probe;
            return;
        }

        for (var d = 0; d < _desiredCount; d++)
        {
            var key = new CoastRegionKey(_desired[d].Tile, _desired[d].X, _desired[d].Z, _baked);

            if (Has(key))
                continue;

            var slot = FreeSlot();

            if (slot < 0)
                return;

            ref var region = ref _regions[slot];
            region.Key = key;
            region.Window = new CoastWindow(_tiles, key);
            region.State = CoastRegionState.Baking;
            region.Fade = 0f;
            var probe = Find(key.Tile, key.X, key.Z);
            _bake.Prepare(_depth, region.Window, key, _atlases[slot], _breakDepth, probe < 0 ? float.NaN : _probes[probe].Min, probe < 0 ? float.NaN : _probes[probe].Max);
            _scheduler.Queue(_bake, 1);
            _scheduler.ExecuteJobs();
            _running = Work.Bake;
            _bakeSlot = slot;
            return;
        }
    }

    private bool Has(in CoastRegionKey key)
    {
        for (var i = 0; i < Capacity; i++)
        {
            if (_regions[i].State is CoastRegionState.Live or CoastRegionState.Baking && _regions[i].Key == key)
                return true;
        }

        return false;
    }

    private int FreeSlot()
    {
        for (var i = 0; i < Capacity; i++)
        {
            if (_regions[i].State == CoastRegionState.Free)
                return i;
        }

        return -1;
    }

    private static int Hash(int tile, short x, short z) => (int)((uint)HashCode.Combine(tile, x, z) & (ProbeSlots - 1));

    private int Find(int tile, short x, short z)
    {
        var h = Hash(tile, x, z);

        for (var i = 0; i < ProbeReach; i++)
        {
            var at = (h + i) & (ProbeSlots - 1);
            ref readonly var entry = ref _probes[at];

            if (entry.Known && entry.Tile == tile && entry.X == x && entry.Z == z)
                return at;
        }

        return -1;
    }

    /// <summary>Files a window's depths over its own entry, or else over the entry of its bucket least recently asked for if none is empty.</summary>
    private void Remember(int tile, short x, short z, float min, float max)
    {
        var into = Find(tile, x, z);

        if (into < 0)
        {
            var h = Hash(tile, x, z);
            var oldest = long.MaxValue;

            for (var i = 0; i < ProbeReach; i++)
            {
                var at = (h + i) & (ProbeSlots - 1);
                var stamp = _probes[at].Known ? _probes[at].Stamp : long.MinValue;

                if (stamp < oldest)
                    (into, oldest) = (at, stamp);
            }
        }

        _probes[into] = new ProbeEntry { Tile = tile, X = x, Z = z, Known = true, Min = min, Max = max, Stamp = ++_stamp };
    }

    public void Dispose() => _scheduler.AbortJobs(waitForCompletion: true);
}
