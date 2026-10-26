using System.Numerics;
using BrutalGame.Engine.Rendering;

namespace BrutalGame.Terrain.Runtime.Water;

/// <summary>
/// Shallow-water (virtual pipe) simulation of the sea next to edits (docs/voxel-water.md, "Sea zones").
/// A 1 m heightfield over the union of zone discs: bed from the terrain, water depth, and an outflow flux per cell side,
/// so water carries momentum, fills at wave speed and has a velocity field. Cells next to open sea are held at the swell
/// height, so waves physically run into the zone. Foam is injected by drops and fast flow and advected by the velocity.
/// Main thread; a few thousand cells, a few hundred microseconds per tick.
/// </summary>
public sealed class ZoneFlowSim
{
    public const float CellSize = 1f;
    public const float Gravity = 9.81f;
    private const float WetDepth = 0.03f;

    /// <summary>Depth floor for turning flux into velocity, and the speed cap: thin films would otherwise read as 200 m/s.</summary>
    private const float VelocityDepth = 0.1f;
    private const float MaxVelocity = 8f;
    private const float RestPadding = 2f;

    /// <summary>Thinnest face (m) drag divides by: films thinner than this are all but stopped.</summary>
    private const float MinFaceDepth = 0.01f;

    /// <summary>Background flux damping (1/s), whatever the depth; keeps a deep pool from sloshing forever.</summary>
    public float FluxDampingRate = 0.2f;

    /// <summary>
    /// Quadratic bed drag coefficient: deceleration Cd u² / h, so thin films and run-up slow down while deep water glides.
    /// </summary>
    public float BedDrag = 0.01f;

    /// <summary>Froude number above which fast flow starts to tumble and entrain air (fully white at twice this).</summary>
    public float FoamFroude = 1.5f;

    /// <summary>Drop height (m) over which falling water churns fully white.</summary>
    public float FoamDrop = 2f;

    /// <summary>Foam added per second at full churn.</summary>
    public float FoamBuild = 1.5f;

    /// <summary>Foam fade per second: aerated water takes a while to settle.</summary>
    public float FoamDecay = 0.2f;

    /// <summary>Distance (m) past a disc edge over which zone foam keeps drifting out onto the open sea, fading.</summary>
    public float FoamSpill = 6f;

    /// <summary>Inflow from the open sea per metre of zone edge (m^2/s) that counts as violent enough to grow the zone there.</summary>
    public float GrowFlux = 1.5f;

    /// <summary>Height (m) a surge must stand above the local swell and the ground past the edge to spill and grow the zone.</summary>
    public float GrowSurge = 0.3f;

    /// <summary>Radius (m) of a disc added when the zone grows, and the minimum time between growth steps.</summary>
    public float GrowRadius = 8f;
    public float GrowCooldown = 0.5f;

    /// <summary>The zone stops growing past this many cells (a budget cap; ~0.1 ms per 1000 cells per tick).</summary>
    public int MaxCells = 12000;

    /// <summary>Growth discs only reach this far (m) past an edit disc's edge.</summary>
    public float GrowReach = 16f;

    /// <summary>Calm thresholds for handing a grown disc back to the ocean: flow speed (m/s), foam, height off the swell (m).</summary>
    public float CalmSpeed = 1.5f;
    public float CalmFoam = 0.15f;
    public float CalmSurge = 0.25f;

    /// <summary>Cells shallower than this (m) are beach wash and are ignored by the calm test.</summary>
    public float CalmMinDepth = 0.3f;

    /// <summary>Surface slope at which a front is steep enough to break (crash) and foam, and the depth fast flow needs to churn.</summary>
    public float BreakSlope = 0.6f;
    public float ChurnDepth = 0.5f;

    /// <summary>Splashes kept per tick; the rest of a long wall's or a wide pour's are dropped.</summary>
    public const int MaxSplashes = 512;

    /// <summary>Height (m) a neighbour's bed must stand over the water to be a wall the water slams into rather than runs up.</summary>
    public float WallHeight = 0.5f;

    /// <summary>Speed (m/s) into a wall over which the water splashes up it, and the shallowest water (m) that does.</summary>
    public float SplashSpeed = 0.4f;
    public float SplashDepth = 0.15f;

    /// <summary>A pour: water falling at least this far (m) onto the water below, with at least this discharge (m²/s per metre of lip).</summary>
    public float PourDrop = 0.5f;
    public float PourDischarge = 0.05f;

    /// <summary>
    /// Turbulence: how agitated the water is (0..1), raised where it falls, crashes, runs supercritical or slams a wall,
    /// carried with the flow and spread by its own eddies (m²/s), dying out at a rate (1/s). The ocean roughens and
    /// foams the surface by it, so a plunge pool stays rough for a while after the pour.
    /// </summary>
    public float TurbulenceBuild = 2f;
    public float TurbulenceSpread = 6f;
    public float TurbulenceDecay = 0.08f;

    /// <summary>
    /// A plunge pool's boil (m/s per unit of turbulence per metre): churned water wells up where it is most turbulent and
    /// spreads out over the surface, carrying the foam away from the fall. The sim's own velocity is the depth average,
    /// nearly still in a deep pool, so the surface drift drawn (<see cref="PackFlow"/>) adds this, up to <see cref="MaxDrift"/>.
    /// </summary>
    public float BoilSpeed = 8f;
    private const float MaxDrift = 3f;

    private readonly WaterSplash[] _splashes = new WaterSplash[MaxSplashes];
    private int _splashCount;

    /// <summary>Seconds a grown disc must stay calm before it remerges, and how often calm is checked.</summary>
    public float RemergeTime = 3f;
    private const float CalmCheck = 0.5f;

    /// <summary>Discs the zone wants to add (violent flow or water spilling past its edge). Consumed by the owner.</summary>
    public readonly List<Vector3> Growth = new();

    /// <summary>Grown discs handed back to the ocean this tick. Consumed by the owner.</summary>
    public readonly List<Vector3> Remerged = new();

    private float _growTimer;

    /// <summary>Open-sea surface height at a world xz (swell); null means flat sea level.</summary>
    public Func<float, float, float>? SeaSurface;

    /// <summary>
    /// The open sea at a world xz: x = swell height (m), yz = its surface velocity (the orbital motion, m/s). New sea cells
    /// start from it, sampled every <see cref="SeedSpacing"/> and interpolated between; null means flat and still.
    /// </summary>
    public Func<float, float, Vector3>? SeaState;

    /// <summary>
    /// Lattice (m) the open sea is sampled on to seed new cells: the swell varies over metres, and a sample a cell cost a
    /// 14 ms tick when two discs grew at once.
    /// </summary>
    private const float SeedSpacing = 4f;

    /// <summary>
    /// Wave period (s) the exchange with the open sea is averaged over. Each wave flows in over the zone's sea edge and
    /// back out within a period, a pour keeps flowing in, so growth and calm read the net inflow averaged twice over this
    /// (<see cref="_sustained"/>), not its swing: read raw, every crest of a 1 m swell (about 3 m²/s) grew the zone.
    /// </summary>
    public float WavePeriod = 8f;

    public float SeaLevel;

    private Vector2 _origin;
    private int _nx;
    private int _nz;
    private float[] _bed = [];
    private float[] _depth = [];
    private float[] _foam = [];
    private float[] _foamNext = [];
    private float[] _turbulence = [];
    private float[] _turbulenceNext = [];

    // Per cell, packed for the GPU once a tick (PackFlow): half (velocity x, z), half (turbulence, 0).
    private uint[] _packed = [];

    // Sim time of the last breaker bore. Surf is the ocean's normal state, not a disturbance: while bores arrive the zone
    // does not grow (growth is for edits draining), or every set of waves would enlarge it and its cost. Zone-wide,
    // because a surge spreads through the sim to other edge cells within the quiet time.
    private float _lastBore = float.NegativeInfinity;
    private float _time;

    /// <summary>Seconds after any bore during which the zone does not grow.</summary>
    public float BoreQuiet = 2f;
    private float[] _flux = [];      // 4 per cell: -X, +X, -Z, +Z outflow (m^3/s)
    private float[] _inflow = [];    // 4 per cell: inflow from an open-sea boundary on that side
    private float[] _exchange = [];  // 4 per cell: net inflow from the open sea on that side, averaged over a wave period ...
    private float[] _sustained = []; // ... and averaged again (m^3/s per metre of edge)
    private float[] _velX = [];
    private float[] _velZ = [];
    private float[] _seaLevelAt = [];
    private byte[] _kind = [];       // 0 = outside, 1 = zone, 2 = open-sea boundary
    private readonly List<Vector3> _discs = new();
    private readonly List<bool> _grown = new();
    private readonly List<float> _calm = new();

    // Grown discs handed back to the ocean, still simulated while the ocean fades them out (RemoveDisc ends them).
    private readonly List<bool> _leaving = new();
    private readonly List<(int X, int Z, float Bed)> _dryCells = new(1024);
    private float _calmTimer;

    private static readonly (int X, int Z)[] Sides = [(-1, 0), (1, 0), (0, -1), (0, 1)];

    public int Cells => _nx * _nz;

    public float MaxSpeed { get; private set; }

    /// <summary>Debug: world x, depth, world z of the fastest cell.</summary>
    public Vector3 MaxSpeedAt { get; private set; }

    public float Volume { get; private set; }

    /// <summary>Mean foam over wet zone cells (0..1): how white the zone reads.</summary>
    public float MeanFoam { get; private set; }

    /// <summary>Where water slammed into a wall or landed from a pour as of the last <see cref="FindSplashes"/>: the ocean renderer throws spray there.</summary>
    public ReadOnlySpan<WaterSplash> Splashes => _splashes.AsSpan(0, _splashCount);

    /// <summary>Mean and highest turbulence over wet zone cells as of the last <see cref="StepTurbulence"/>.</summary>
    public float MeanTurbulence { get; private set; }

    public float MaxTurbulence { get; private set; }

    /// <summary>
    /// The sim's flow for the GPU as of the last <see cref="PackFlow"/>: (world x, world z of the grid's corner, cell size,
    /// cells across), and per cell two words, half (surface drift x, z) and half (turbulence, 0), row by row.
    /// </summary>
    public Vector4 FlowRect => new(_origin.X, _origin.Y, CellSize, _nx);

    public ReadOnlySpan<uint> PackedFlow => _packed.AsSpan(0, 2 * Cells);

    /// <summary>Total volume (m³) breaker bores have moved shoreward inside the zone (docs/breaking-waves.md, step 5).</summary>
    public float InjectedVolume { get; private set; }

    /// <summary>
    /// A breaker's bore arriving in the zone. Mass-conserving: up to <paramref name="volume"/> (m³) moves from the zone
    /// cells behind <paramref name="position"/> (against <paramref name="velocity"/>) to the cells ahead, at most half of
    /// what is behind; outflow flux on all of them is raised so the water keeps moving at <paramref name="velocity"/>
    /// (the step's limiter caps it to what each cell holds); foam is added. Net wave mass already arrives through the
    /// sea boundary, and a real bore's return flow takes back what it carries, so nothing is created here.
    /// Returns false where there is no zone water.
    /// </summary>
    /// <param name="thickness">Depth of the moving layer (about the bore height): only it carries the bore's momentum.</param>
    public bool Inject(Vector2 position, float volume, Vector2 velocity, float thickness, float foam, float radius)
    {
        if (_nx == 0)
            return false;

        var speed = velocity.Length();
        var dir = speed > 1e-4f ? velocity / speed : Vector2.Zero;
        var behind = position - dir * radius;
        var ahead = position + dir * radius;
        var available = 0f;
        var targets = 0;

        Span<int> region = stackalloc int[4];
        CellRange(position, 2f * radius, region);

        for (var z = region[1]; z <= region[3]; z++)
        {
            for (var x = region[0]; x <= region[2]; x++)
            {
                var i = x + z * _nx;
                if (_kind[i] != 1)
                    continue;

                var c = CellCentre(x, z);
                if (Vector2.DistanceSquared(c, behind) <= radius * radius)
                    available += MathF.Max(0f, _depth[i] - WetDepth);
                else if (Vector2.DistanceSquared(c, ahead) <= radius * radius)
                    targets++;
            }
        }

        if (targets == 0 && available <= 0f)
            return false;

        var area = CellSize * CellSize;
        var moved = targets > 0 ? MathF.Min(volume, 0.5f * available * area) : 0f;
        var take = available > 0f ? moved / (available * area) : 0f;
        var give = targets > 0 ? moved / (targets * area) : 0f;

        for (var z = region[1]; z <= region[3]; z++)
        {
            for (var x = region[0]; x <= region[2]; x++)
            {
                var i = x + z * _nx;
                var c = CellCentre(x, z);
                if (_kind[i] != 1 || Vector2.DistanceSquared(c, position) > 4f * radius * radius)
                    continue;

                if (Vector2.DistanceSquared(c, behind) <= radius * radius)
                    _depth[i] -= MathF.Max(0f, _depth[i] - WetDepth) * take;
                else if (Vector2.DistanceSquared(c, ahead) <= radius * radius)
                    _depth[i] += give;

                _foam[i] = MathF.Min(1f, _foam[i] + foam);

                for (var s = 0; s < 4; s++)
                {
                    var along = velocity.X * Sides[s].X + velocity.Y * Sides[s].Z;
                    if (along > 0f)
                        _flux[i * 4 + s] = MathF.Max(_flux[i * 4 + s], along * MathF.Min(_depth[i], thickness) * CellSize);
                }
            }
        }

        InjectedVolume += moved;
        _lastBore = _time;
        return true;
    }

    /// <summary>Cell index bounds (x0, z0, x1, z1) of a square of half-size <paramref name="reach"/>, clamped to the grid.</summary>
    private void CellRange(Vector2 centre, float reach, Span<int> range)
    {
        range[0] = Math.Max(0, (int)MathF.Floor((centre.X - reach - _origin.X) / CellSize));
        range[1] = Math.Max(0, (int)MathF.Floor((centre.Y - reach - _origin.Y) / CellSize));
        range[2] = Math.Min(_nx - 1, (int)MathF.Floor((centre.X + reach - _origin.X) / CellSize));
        range[3] = Math.Min(_nz - 1, (int)MathF.Floor((centre.Y + reach - _origin.Y) / CellSize));
    }

    /// <summary>Water depth (m) of the zone cell at a world position; 0 outside the zone.</summary>
    public float DepthAt(Vector2 position)
    {
        var x = (int)MathF.Floor((position.X - _origin.X) / CellSize);
        var z = (int)MathF.Floor((position.Y - _origin.Y) / CellSize);
        var i = Neighbour(x, z);
        return i >= 0 && _kind[i] == 1 ? _depth[i] : 0f;
    }

    /// <summary>
    /// Adds a zone disc. New sea cells start as the open sea stands there now, its swell height and orbital flow, over
    /// the bed as it is now (before the edit lands), so the sim takes the waves over where they are (starting from flat
    /// sea level popped every wave in the disc flat) and a hole dug later reads as the bed dropping under the water.
    /// </summary>
    /// <param name="grown">Added by growth rather than an edit: it remerges into the ocean once its water is calm.</param>
    public void AddDisc(Vector3 disc, BathymetryGrid grid, bool grown = false)
    {
        _discs.Add(disc);
        _grown.Add(grown);
        _calm.Add(0f);
        _leaving.Add(false);
        ResizeToDiscs(grid);
    }

    /// <summary>Ends a disc the ocean has finished fading out (see <see cref="Remerged"/>).</summary>
    public void RemoveDisc(Vector3 disc, BathymetryGrid grid)
    {
        var d = _discs.IndexOf(disc);
        if (d < 0)
            return;

        _discs.RemoveAt(d);
        _grown.RemoveAt(d);
        _calm.RemoveAt(d);
        _leaving.RemoveAt(d);
        ResizeToDiscs(grid);
    }

    /// <summary>Shrinks or grows the heightfield to the discs' bounds; water in cells still inside a disc is kept.</summary>
    private void ResizeToDiscs(BathymetryGrid grid)
    {
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);

        foreach (var disc in _discs)
        {
            min = Vector2.Min(min, new Vector2(MathF.Floor(disc.X - disc.Z - RestPadding), MathF.Floor(disc.Y - disc.Z - RestPadding)));
            max = Vector2.Max(max, new Vector2(MathF.Ceiling(disc.X + disc.Z + RestPadding), MathF.Ceiling(disc.Y + disc.Z + RestPadding)));
        }

        Resize(min, max, grid);
    }

    /// <summary>
    /// Every <see cref="CalmCheck"/> s: a grown disc whose water sits at the swell with slow flow, little foam and no
    /// sustained inflow for <see cref="RemergeTime"/> s is handed back to the ocean (one per check), which fades it out
    /// before <see cref="RemoveDisc"/> ends it. Edit discs stay.
    /// </summary>
    public void CheckRemerge(BathymetryGrid grid, float dt)
    {
        _calmTimer -= dt;
        if (_calmTimer > 0f)
            return;

        _calmTimer = CalmCheck;

        for (var d = _discs.Count - 1; d >= 0; d--)
        {
            if (!_grown[d] || _leaving[d])
                continue;

            _calm[d] = DiscCalm(_discs[d]) ? _calm[d] + CalmCheck : 0f;
        }

        for (var d = _discs.Count - 1; d >= 0; d--)
        {
            if (!_grown[d] || _leaving[d] || _calm[d] < RemergeTime)
                continue;

            Remerged.Add(_discs[d]);
            _leaving[d] = true;
            return;
        }
    }

    private bool DiscCalm(Vector3 disc)
    {
        var x0 = Math.Max(0, (int)((disc.X - disc.Z - _origin.X) / CellSize));
        var z0 = Math.Max(0, (int)((disc.Y - disc.Z - _origin.Y) / CellSize));
        var x1 = Math.Min(_nx - 1, (int)((disc.X + disc.Z - _origin.X) / CellSize));
        var z1 = Math.Min(_nz - 1, (int)((disc.Y + disc.Z - _origin.Y) / CellSize));

        for (var z = z0; z <= z1; z++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var i = x + z * _nx;
                var world = CellCentre(x, z);
                if (_kind[i] != 1 || _depth[i] < WetDepth || Vector2.DistanceSquared(world, new Vector2(disc.X, disc.Y)) > disc.Z * disc.Z)
                    continue;

                // Wash and surf on the beach (shallow cells) is the ocean's normal state, not a disturbance.
                if (_depth[i] < CalmMinDepth)
                    continue;

                var speed = MathF.Sqrt(_velX[i] * _velX[i] + _velZ[i] * _velZ[i]);
                var sea = SeaLevel + (SeaSurface?.Invoke(world.X, world.Y) ?? 0f);

                if (speed > CalmSpeed || MathF.Abs(_bed[i] + _depth[i] - sea) > CalmSurge)
                    return false;

                if (_depth[i] > 1f && _foam[i] > CalmFoam)
                    return false;

                for (var s = 0; s < 4; s++)
                    if (_sustained[i * 4 + s] > GrowFlux * 0.5f)
                        return false;
            }
        }

        return true;
    }

    /// <summary>Growth must stay within this distance (m) past the edge of an edit disc, so a pour cannot creep forever.</summary>
    private bool WithinReach(Vector2 centre)
    {
        for (var d = 0; d < _discs.Count; d++)
        {
            var disc = _discs[d];
            if (!_grown[d] && Vector2.Distance(centre, new Vector2(disc.X, disc.Y)) <= disc.Z + GrowReach)
                return true;
        }

        return false;
    }

    private void Resize(Vector2 min, Vector2 max, BathymetryGrid grid)
    {
        var nx = (int)((max.X - min.X) / CellSize);
        var nz = (int)((max.Y - min.Y) / CellSize);
        var oldOrigin = _origin;
        var (oldNx, oldNz) = (_nx, _nz);
        var (oldDepth, oldFoam, oldFlux, oldKind, oldTurbulence) = (_depth, _foam, _flux, _kind, _turbulence);
        var (oldExchange, oldSustained) = (_exchange, _sustained);
        var lattice = new Seeds(min, nx * CellSize, nz * CellSize, SeaState);

        _origin = min;
        _nx = nx;
        _nz = nz;
        var n = nx * nz;
        _bed = new float[n];
        _depth = new float[n];
        _foam = new float[n];
        _foamNext = new float[n];
        _turbulence = new float[n];
        _turbulenceNext = new float[n];
        _packed = new uint[2 * n];
        _flux = new float[n * 4];
        _inflow = new float[n * 4];
        _exchange = new float[n * 4];
        _sustained = new float[n * 4];
        _velX = new float[n];
        _velZ = new float[n];
        _seaLevelAt = new float[n];
        _kind = new byte[n];

        for (var z = 0; z < nz; z++)
        {
            for (var x = 0; x < nx; x++)
            {
                var i = x + z * nx;
                var world = CellCentre(x, z);
                _bed[i] = grid.SampleGround(world.X, world.Y);
                _kind[i] = InAnyDisc(world) ? (byte)1 : (byte)0;

                var ox = (int)((world.X - oldOrigin.X) / CellSize);
                var oz = (int)((world.Y - oldOrigin.Y) / CellSize);
                var old = (uint)ox < (uint)oldNx && (uint)oz < (uint)oldNz ? ox + oz * oldNx : -1;

                if (old >= 0 && oldKind[old] == 1)
                {
                    _depth[i] = oldDepth[old];
                    _foam[i] = oldFoam[old];
                    _turbulence[i] = oldTurbulence[old];
                    oldFlux.AsSpan(old * 4, 4).CopyTo(_flux.AsSpan(i * 4, 4));
                    oldExchange.AsSpan(old * 4, 4).CopyTo(_exchange.AsSpan(i * 4, 4));
                    oldSustained.AsSpan(old * 4, 4).CopyTo(_sustained.AsSpan(i * 4, 4));
                }
                else if (_kind[i] == 1 && grid.IsSea(world.X, world.Y))
                {
                    // Only open sea starts full, as the swell has it now; dry ground and inland pits the zone grows over
                    // start dry and fill by flow.
                    var sea = lattice.At(world);
                    _depth[i] = MathF.Max(0f, SeaLevel + sea.X - _bed[i]);
                    var flow = new Vector2(sea.Y, sea.Z) * (_depth[i] * CellSize);
                    _flux[i * 4] = MathF.Max(-flow.X, 0f);
                    _flux[i * 4 + 1] = MathF.Max(flow.X, 0f);
                    _flux[i * 4 + 2] = MathF.Max(-flow.Y, 0f);
                    _flux[i * 4 + 3] = MathF.Max(flow.Y, 0f);
                }
            }
        }

        // Cells just outside the zone that are open sea become fixed-level boundaries; the rest are walls.
        for (var z = 0; z < nz; z++)
            for (var x = 0; x < nx; x++)
            {
                var i = x + z * nx;
                if (_kind[i] == 0 && _bed[i] < SeaLevel && grid.IsSea(CellCentre(x, z).X, CellCentre(x, z).Y) && TouchesZone(x, z))
                    _kind[i] = 2;
            }
    }

    private bool TouchesZone(int x, int z)
    {
        foreach (var (dx, dz) in Sides)
        {
            var nx = x + dx;
            var nz = z + dz;
            if ((uint)nx < (uint)_nx && (uint)nz < (uint)_nz && _kind[nx + nz * _nx] == 1)
                return true;
        }

        return false;
    }

    private bool InAnyDisc(Vector2 world)
    {
        foreach (var disc in _discs)
            if (Vector2.DistanceSquared(world, new Vector2(disc.X, disc.Y)) <= disc.Z * disc.Z)
                return true;

        return false;
    }

    private Vector2 CellCentre(int x, int z) => _origin + new Vector2((x + 0.5f) * CellSize, (z + 0.5f) * CellSize);

    /// <summary>
    /// Re-reads the bed after terrain edits land. Where the bed drops, water depth is kept, so the surface drops and the
    /// sea flows in. Where it rises, the surface is kept and the water is displaced (it does not ride up on new ground).
    /// </summary>
    public void RefreshBed(BathymetryGrid grid)
    {
        for (var z = 0; z < _nz; z++)
            for (var x = 0; x < _nx; x++)
            {
                var i = x + z * _nx;
                var world = CellCentre(x, z);
                var bed = grid.SampleGround(world.X, world.Y);

                if (bed > _bed[i])
                    _depth[i] = MathF.Max(0f, _bed[i] + _depth[i] - bed);

                _bed[i] = bed;
            }
    }

    /// <summary>Samples the open-sea surface at boundary cells; once per tick.</summary>
    public void UpdateBoundary()
    {
        for (var z = 0; z < _nz; z++)
            for (var x = 0; x < _nx; x++)
            {
                var i = x + z * _nx;
                if (_kind[i] != 2)
                    continue;

                var world = CellCentre(x, z);
                _seaLevelAt[i] = SeaLevel + (SeaSurface?.Invoke(world.X, world.Y) ?? 0f);
            }
    }

    public void Step(float dt)
    {
        if (_nx == 0)
            return;

        var area = CellSize * CellSize;
        var pipe = dt * Gravity;
        var damping = MathF.Exp(-FluxDampingRate * dt);
        var settle = 1f - MathF.Exp(-dt / MathF.Max(WavePeriod, 1f));

        // 1. Outflow fluxes from height differences (momentum carried in the fluxes), limited to the water available.
        // Each pipe's cross-section is the water standing over the higher of the two beds (hydrostatic reconstruction):
        // deep water conducts as deep water (waves at √(g h)), and a film over a ledge only spills what stands on it.
        for (var z = 0; z < _nz; z++)
        {
            for (var x = 0; x < _nx; x++)
            {
                var i = x + z * _nx;
                if (_kind[i] != 1)
                    continue;

                var surface = _bed[i] + _depth[i];
                var total = 0f;

                for (var s = 0; s < 4; s++)
                {
                    var (dx, dz) = Sides[s];
                    var j = Neighbour(x + dx, z + dz);
                    ref var f = ref _flux[i * 4 + s];
                    ref var fin = ref _inflow[i * 4 + s];

                    if (j < 0 || _kind[j] == 0)
                    {
                        f = 0f;
                        fin = 0f;
                        continue;
                    }

                    var other = _kind[j] == 2 ? _seaLevelAt[j] : _bed[j] + _depth[j];
                    var face = MathF.Max(0f, MathF.Max(surface, other) - MathF.Max(_bed[i], _bed[j]));
                    f = MathF.Max(0f, f * Drag(f, face, damping, dt) + pipe * face * (surface - other));

                    // Open sea on this side: an infinite reservoir pushing water in.
                    fin = _kind[j] == 2 ? MathF.Max(0f, fin * Drag(fin, face, damping, dt) + pipe * face * (other - surface)) : 0f;
                    total += f;
                }

                if (total > 0f)
                {
                    var limit = MathF.Min(1f, _depth[i] * area / (total * dt));
                    for (var s = 0; s < 4; s++)
                        _flux[i * 4 + s] *= limit;
                }
            }
        }

        // 2. Volume update and velocity from the fluxes through each face.
        var maxSpeed = 0f;
        var volume = 0f;
        Span<float> through = stackalloc float[4]; // net flow out through each side

        for (var z = 0; z < _nz; z++)
        {
            for (var x = 0; x < _nx; x++)
            {
                var i = x + z * _nx;
                if (_kind[i] != 1)
                    continue;

                var flowIn = 0f;
                var flowOut = 0f;

                for (var s = 0; s < 4; s++)
                {
                    var (dx, dz) = Sides[s];
                    var j = Neighbour(x + dx, z + dz);
                    var into = 0f;
                    var k = i * 4 + s;

                    if (j >= 0 && _kind[j] == 1)
                        into = _flux[j * 4 + (s ^ 1)];
                    else if (j >= 0 && _kind[j] == 2)
                        into = _inflow[k];

                    var exchange = j >= 0 && _kind[j] == 2 ? into - _flux[k] : 0f;
                    _exchange[k] += (exchange - _exchange[k]) * settle;
                    _sustained[k] += (_exchange[k] - _sustained[k]) * settle;

                    flowIn += into;
                    flowOut += _flux[i * 4 + s];
                    through[s] = _flux[i * 4 + s] - into;
                }

                var before = _depth[i];
                _depth[i] = MathF.Max(0f, before + dt * (flowIn - flowOut) / area);

                var mean = MathF.Max(0.5f * (before + _depth[i]), VelocityDepth);
                _velX[i] = 0.5f * (through[1] - through[0]) / (CellSize * mean);
                _velZ[i] = 0.5f * (through[3] - through[2]) / (CellSize * mean);

                // Thin run-up films move at most around their own wave speed; flux / tiny depth would otherwise read huge.
                var speed = MathF.Sqrt(_velX[i] * _velX[i] + _velZ[i] * _velZ[i]);
                var cap = MathF.Min(MaxVelocity, 2f * MathF.Sqrt(Gravity * MathF.Max(_depth[i], WetDepth)));
                if (speed > cap)
                {
                    var scale = cap / speed;
                    _velX[i] *= scale;
                    _velZ[i] *= scale;
                    speed = cap;
                }
                if (_depth[i] < WetDepth)
                {
                    _velX[i] = 0f;
                    _velZ[i] = 0f;
                    speed = 0f;
                }

                if (speed > maxSpeed)
                    MaxSpeedAt = new Vector3(CellCentre(x, z).X, _depth[i], CellCentre(x, z).Y);

                maxSpeed = MathF.Max(maxSpeed, speed);
                volume += _depth[i] * area;
            }
        }

        MaxSpeed = maxSpeed;
        Volume = volume;
        StepFoam(dt);
        DetectGrowth(dt);
    }

    /// <summary>Implicit bed drag on a face flux (u = f / (width × face depth); du/dt = −Cd u² / h) times background damping.</summary>
    private float Drag(float flux, float face, float damping, float dt)
    {
        var depth = MathF.Max(face, MinFaceDepth);
        return damping / (1f + dt * BedDrag * flux / (CellSize * depth * depth));
    }

    /// <summary>
    /// Once per tick, after the substeps: where the water slams, it splashes and turns turbulent. Splashes are listed
    /// where it runs into a wall (a side whose bed stands <see cref="WallHeight"/> over the surface, so it can only go up
    /// it; a beach it runs up instead) and where a pour lands (water arriving from <see cref="PourDrop"/> or more above).
    /// Turbulence is raised there and where flow is supercritical, carried with the flow, spread and left to die out.
    /// </summary>
    public void Agitate(float dt)
    {
        _splashCount = 0;
        var decay = MathF.Exp(-TurbulenceDecay * dt);
        var spread = TurbulenceSpread * dt / (CellSize * CellSize);
        var total = 0f;
        var highest = 0f;
        var wet = 0;

        for (var z = 0; z < _nz; z++)
        {
            for (var x = 0; x < _nx; x++)
            {
                var i = x + z * _nx;
                if (_kind[i] != 1 || _depth[i] < WetDepth)
                {
                    _turbulenceNext[i] = 0f;
                    continue;
                }

                var surface = _bed[i] + _depth[i];
                var speed = MathF.Sqrt(_velX[i] * _velX[i] + _velZ[i] * _velZ[i]);
                var froude = speed / MathF.Sqrt(Gravity * MathF.Max(_depth[i], WetDepth));
                var source = Ramp((froude - FoamFroude) / FoamFroude) * Ramp(_depth[i] / ChurnDepth);
                var eddies = 0f;

                for (var s = 0; s < 4; s++)
                {
                    var (dx, dz) = Sides[s];
                    var j = Neighbour(x + dx, z + dz);
                    if (j < 0)
                        continue;

                    if (_kind[j] == 1 && _depth[j] >= WetDepth)
                    {
                        eddies += _turbulence[j] - _turbulence[i];

                        var discharge = _flux[j * 4 + (s ^ 1)] / CellSize;
                        var fall = _bed[j] + _depth[j] - surface;
                        if (fall < PourDrop || discharge < PourDischarge)
                            continue;

                        var landing = MathF.Min(MathF.Sqrt(2f * Gravity * fall), 12f);
                        source = MathF.Max(source, Ramp(fall / FoamDrop) * Ramp(discharge / 0.5f));
                        AddSplash(new Vector4(CellCentre(x, z).X, surface, CellCentre(x, z).Y, landing), new Vector4(-dx, -dz, discharge, WaterSplash.Pour));
                        continue;
                    }

                    var into = _velX[i] * dx + _velZ[i] * dz;
                    if (_depth[i] < SplashDepth || into < SplashSpeed || _bed[j] < surface + WallHeight)
                        continue;

                    var foot = CellCentre(x, z) + new Vector2(dx, dz) * (0.5f * CellSize);
                    source = MathF.Max(source, Ramp((into - SplashSpeed) / 2f));
                    AddSplash(new Vector4(foot.X, surface, foot.Y, into), new Vector4(dx, dz, _depth[i], WaterSplash.Wall));
                }

                // Carried with the flow (semi-Lagrangian, as foam is), spread by its eddies to the wet neighbours.
                var back = new Vector2(x, z) - new Vector2(_velX[i], _velZ[i]) * (dt / CellSize);
                var carried = Sample(_turbulence, back.X, back.Y);
                var next = MathF.Min(1f, (carried + spread * eddies) * decay + source * TurbulenceBuild * dt);
                _turbulenceNext[i] = next;
                total += next;
                highest = MathF.Max(highest, next);
                wet++;
            }
        }

        (_turbulence, _turbulenceNext) = (_turbulenceNext, _turbulence);
        MeanTurbulence = wet > 0 ? total / wet : 0f;
        MaxTurbulence = highest;
    }

    private void AddSplash(Vector4 positionSpeed, Vector4 direction)
    {
        if (_splashCount < MaxSplashes)
            _splashes[_splashCount++] = new WaterSplash { PositionSpeed = positionSpeed, Direction = direction };
    }

    /// <summary>
    /// Packs the surface drift (velocity plus the boil down the turbulence gradient) and turbulence for the GPU
    /// (<see cref="PackedFlow"/>). Once per tick, after <see cref="Agitate"/>.
    /// </summary>
    public void PackFlow()
    {
        for (var z = 0; z < _nz; z++)
        {
            for (var x = 0; x < _nx; x++)
            {
                var i = x + z * _nx;
                var drift = new Vector2(_velX[i], _velZ[i]);
                var turbulence = _turbulence[i];

                if (turbulence > 0f)
                {
                    var slope = new Vector2(
                        TurbulenceAt(x + 1, z, turbulence) - TurbulenceAt(x - 1, z, turbulence),
                        TurbulenceAt(x, z + 1, turbulence) - TurbulenceAt(x, z - 1, turbulence));
                    drift -= slope * (0.5f * BoilSpeed / CellSize);

                    var speed = drift.Length();
                    if (speed > MaxDrift)
                        drift *= MaxDrift / speed;
                }

                _packed[2 * i] = PackHalves(drift.X, drift.Y);
                _packed[2 * i + 1] = PackHalves(turbulence, 0f);
            }
        }
    }

    /// <summary>Turbulence of cell (x, z), or <paramref name="outside"/> beyond the grid.</summary>
    private float TurbulenceAt(int x, int z, float outside)
    {
        var i = Neighbour(x, z);
        return i >= 0 ? _turbulence[i] : outside;
    }

    private static uint PackHalves(float low, float high) =>
        BitConverter.HalfToUInt16Bits((Half)low) | (uint)BitConverter.HalfToUInt16Bits((Half)high) << 16;

    /// <summary>
    /// Looks at zone cells on the edge: fast exchange with the open sea, or water standing above an outside cell's bed
    /// (it would keep flowing inland), asks for a new disc there. Terrain placed to block the flow raises the bed and so
    /// stops the growth.
    /// </summary>
    private void DetectGrowth(float dt)
    {
        _time += dt;
        _growTimer -= dt;
        if (_growTimer > 0f || Cells >= MaxCells || _time - _lastBore < BoreQuiet)
            return;

        var best = 0f;
        var bestCell = -1;

        for (var z = 0; z < _nz; z++)
        {
            for (var x = 0; x < _nx; x++)
            {
                var i = x + z * _nx;
                if (_kind[i] != 1 || _depth[i] < 0.2f)
                    continue;

                var surface = _bed[i] + _depth[i];

                for (var s = 0; s < 4; s++)
                {
                    var (dx, dz) = Sides[s];
                    var j = Neighbour(x + dx, z + dz);
                    if (j < 0 || _kind[j] == 1)
                        continue;

                    // Sea side: inflow per metre of edge. Land side: a surge standing above both the ground beyond and the
                    // local swell (plain sea level against a low beach is the rest state, not a spill).
                    float score;
                    if (_kind[j] == 2)
                    {
                        score = _sustained[i * 4 + s] / GrowFlux;
                    }
                    else
                    {
                        var world = CellCentre(x, z);
                        var sea = SeaLevel + (SeaSurface?.Invoke(world.X, world.Y) ?? 0f);
                        score = MathF.Min(surface - _bed[j], surface - sea) / GrowSurge;
                    }

                    if (score > 1f && score > best && WithinReach(CellCentre(x + dx, z + dz)))
                    {
                        best = score;
                        bestCell = j;
                    }
                }
            }
        }

        if (bestCell < 0)
            return;

        var centre = CellCentre(bestCell % _nx, bestCell / _nx);
        Growth.Add(new Vector3(centre.X, centre.Y, GrowRadius));
        _growTimer = GrowCooldown;
    }

    /// <summary>Inject foam where water falls over a drop or runs fast, advect it with the flow, let it settle.</summary>
    private void StepFoam(float dt)
    {
        var decay = MathF.Exp(-FoamDecay * dt);
        var total = 0f;
        var wet = 0;

        for (var z = 0; z < _nz; z++)
        {
            for (var x = 0; x < _nx; x++)
            {
                var i = x + z * _nx;
                if (_kind[i] != 1 || _depth[i] < WetDepth)
                {
                    _foamNext[i] = 0f;
                    continue;
                }

                // Semi-Lagrangian: take the foam from where this water came from.
                var back = new Vector2(x, z) - new Vector2(_velX[i], _velZ[i]) * (dt / CellSize);
                var carried = Sample(_foam, back.X, back.Y);

                // Moving water is not foam: only supercritical flow (Froude number well over 1, where it tumbles and
                // jumps) with depth behind it entrains air. A deep pool swirling at a few m/s stays clear.
                var speed = MathF.Sqrt(_velX[i] * _velX[i] + _velZ[i] * _velZ[i]);
                var froude = speed / MathF.Sqrt(Gravity * MathF.Max(_depth[i], WetDepth));
                var churn = Ramp((froude - FoamFroude) / FoamFroude) * Ramp(_depth[i] / ChurnDepth);

                // Water arriving from a neighbour whose surface stands well above this one is falling: a pour (aerates
                // by the height of the fall) or a front steep enough to crash. Once levels even out, the churn stops.
                var here = _bed[i] + _depth[i];
                for (var s = 0; s < 4; s++)
                {
                    var (dx, dz) = Sides[s];
                    var j = Neighbour(x + dx, z + dz);
                    if (j < 0 || _kind[j] != 1)
                        continue;

                    var into = _flux[j * 4 + (s ^ 1)];
                    var fall = _bed[j] + _depth[j] - here;
                    if (into > 0.01f && fall > 0.5f)
                        churn = MathF.Max(churn, Ramp(fall / FoamDrop) * Ramp(into / 0.5f));
                    else if (into > 0.01f && _depth[j] > WetDepth)
                        churn = MathF.Max(churn, Ramp((fall / CellSize - BreakSlope) / BreakSlope) * Ramp(into / 0.2f));
                }

                _foamNext[i] = MathF.Min(1f, carried * decay + churn * FoamBuild * dt);
                total += _foamNext[i];
                wet++;
            }
        }

        (_foam, _foamNext) = (_foamNext, _foam);
        MeanFoam = wet > 0 ? total / wet : 0f;
    }

    /// <summary>A per-cell field bilinear at fractional cell coordinates, clamped to the grid.</summary>
    private float Sample(float[] field, float fx, float fz)
    {
        fx = Math.Clamp(fx, 0f, _nx - 1.001f);
        fz = Math.Clamp(fz, 0f, _nz - 1.001f);
        var x0 = (int)fx;
        var z0 = (int)fz;
        var tx = fx - x0;
        var tz = fz - z0;
        var a = field[x0 + z0 * _nx];
        var b = field[x0 + 1 + z0 * _nx];
        var c = field[x0 + (z0 + 1) * _nx];
        var d = field[x0 + 1 + (z0 + 1) * _nx];
        return float.Lerp(float.Lerp(a, b, tx), float.Lerp(c, d, tx), tz);
    }

    private int Neighbour(int x, int z) => (uint)x < (uint)_nx && (uint)z < (uint)_nz ? x + z * _nx : -1;

    /// <summary>The open sea (<see cref="SeaState"/>) on a <see cref="SeedSpacing"/> lattice over a resized domain, sampled where first needed, bilinear between.</summary>
    private sealed class Seeds(Vector2 origin, float width, float depth, Func<float, float, Vector3>? sea)
    {
        private readonly int _columns = (int)(width / SeedSpacing) + 2;
        private readonly Vector3[] _values = new Vector3[((int)(width / SeedSpacing) + 2) * ((int)(depth / SeedSpacing) + 2)];
        private readonly bool[] _sampled = new bool[((int)(width / SeedSpacing) + 2) * ((int)(depth / SeedSpacing) + 2)];

        public Vector3 At(Vector2 world)
        {
            if (sea is null)
                return Vector3.Zero;

            var f = (world - origin) / SeedSpacing;
            var x = Math.Clamp((int)f.X, 0, _columns - 2);
            var z = Math.Clamp((int)f.Y, 0, _values.Length / _columns - 2);
            var t = Vector2.Clamp(f - new Vector2(x, z), Vector2.Zero, Vector2.One);
            return Vector3.Lerp(Vector3.Lerp(Point(x, z), Point(x + 1, z), t.X), Vector3.Lerp(Point(x, z + 1), Point(x + 1, z + 1), t.X), t.Y);
        }

        private Vector3 Point(int x, int z)
        {
            var k = x + z * _columns;
            if (!_sampled[k])
            {
                _values[k] = sea!(origin.X + x * SeedSpacing, origin.Y + z * SeedSpacing);
                _sampled[k] = true;
            }

            return _values[k];
        }
    }

    private static float Ramp(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Writes the zone into the bathymetry grid's 2 m cells: wet flag, surface height, depth and foam.</summary>
    public void Publish(BathymetryGrid grid)
    {
        if (_nx == 0)
            return;

        foreach (var disc in _discs)
        {
            var cells = grid.CellsInDisc(disc.X, disc.Y, disc.Z);
            for (var gz = cells.MinZ; gz <= cells.MaxZ; gz++)
            {
                for (var gx = cells.MinX; gx <= cells.MaxX; gx++)
                {
                    var centre = grid.CellCentre(gx, gz);
                    if (!InAnyDisc(centre))
                        continue;

                    // Average the 2x2 sim cells inside this grid cell.
                    var depth = 0f;
                    var surface = 0f;
                    var foam = 0f;
                    var bed = float.MaxValue;
                    var wet = 0;

                    for (var k = 0; k < 4; k++)
                    {
                        var world = centre + new Vector2((k & 1) - 0.5f, (k >> 1) - 0.5f) * (BathymetryGrid.CellSize * 0.5f);
                        var x = (int)MathF.Floor((world.X - _origin.X) / CellSize);
                        var z = (int)MathF.Floor((world.Y - _origin.Y) / CellSize);
                        var i = Neighbour(x, z);
                        if (i < 0 || _kind[i] != 1)
                            continue;

                        bed = MathF.Min(bed, _bed[i]);
                        if (_depth[i] < WetDepth)
                            continue;

                        depth += _depth[i];
                        surface += _bed[i] + _depth[i];
                        foam += _foam[i];
                        wet++;
                    }

                    if (wet > 0)
                        grid.SetZoneLevel(gx, gz, surface / wet, depth / wet, foam / wet);
                    else if (bed < float.MaxValue)
                        _dryCells.Add((gx, gz, bed));
                }
            }
        }

        PublishDry(grid);
        PublishSpill(grid);
    }

    /// <summary>
    /// Dry cells hold the (discarded) surface a metre under their ground, so the interpolated surface dives into it, but
    /// never above the water beside them: a cave wall's ground is the hill over it, and the surface stretched up the wall.
    /// </summary>
    private void PublishDry(BathymetryGrid grid)
    {
        const int n = BathymetryGrid.Resolution;
        var wetCells = grid.ZoneWet;
        var levels = grid.ZoneOffset;

        foreach (var (gx, gz, bed) in _dryCells)
        {
            var surface = bed - 1f;

            for (var z = Math.Max(gz - 1, 0); z <= Math.Min(gz + 1, n - 1); z++)
                for (var x = Math.Max(gx - 1, 0); x <= Math.Min(gx + 1, n - 1); x++)
                    if (wetCells[x + z * n])
                        surface = MathF.Min(surface, levels[x + z * n]);

            grid.SetZoneDry(gx, gz, surface);
        }

        _dryCells.Clear();
    }

    /// <summary>
    /// Foam past the zone edge: each cell in the spill ring takes the foam just inside the nearest disc edge, fading with
    /// distance, so the foam drifts out onto the ocean instead of ending at a circle.
    /// </summary>
    private void PublishSpill(BathymetryGrid grid)
    {
        foreach (var disc in _discs)
        {
            var centre = new Vector2(disc.X, disc.Y);
            var cells = grid.CellsInDisc(disc.X, disc.Y, disc.Z + FoamSpill);

            for (var gz = cells.MinZ; gz <= cells.MaxZ; gz++)
            {
                for (var gx = cells.MinX; gx <= cells.MaxX; gx++)
                {
                    var world = grid.CellCentre(gx, gz);
                    var distance = Vector2.Distance(world, centre);
                    if (distance <= disc.Z || distance > disc.Z + FoamSpill || InAnyDisc(world))
                        continue;

                    var inside = centre + (world - centre) * ((disc.Z - 1f) / distance);
                    var x = (int)MathF.Floor((inside.X - _origin.X) / CellSize);
                    var z = (int)MathF.Floor((inside.Y - _origin.Y) / CellSize);
                    var i = Neighbour(x, z);
                    var edgeFoam = i >= 0 && _kind[i] == 1 ? _foam[i] : 0f;
                    var fade = 1f - (distance - disc.Z) / FoamSpill;
                    grid.SetSpillFoam(gx, gz, edgeFoam * fade * fade);
                }
            }
        }
    }
}
