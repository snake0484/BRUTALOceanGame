using System.Numerics;
using System.Runtime.CompilerServices;

namespace BrutalGame.Terrain.Water;

/// <summary>
/// A body of still water the generator made: its centre and fill radius (world x, z), the world y of its surface, its
/// bottom, and its surface area (m²). The sim registers each as a <see cref="WaterBodies"/> entry and holds the generated
/// water inside its radius at that body's level.
/// </summary>
public readonly record struct WaterBodyInfo(Vector2 Centre, float Radius, float Level, float Bottom, float Area)
{
    /// <summary>Set by the sim when it registers the body.</summary>
    public ushort Id { get; init; }

    public bool Contains(float x, float z)
    {
        var dx = x - Centre.X;
        var dz = z - Centre.Y;
        return dx * dx + dz * dz <= Radius * Radius;
    }
}

/// <summary>
/// Bodies of still water (docs/voxel-water.md "Bodies of water"): each has one surface level, and the cells it holds
/// (<see cref="WaterFlags.Reservoir"/>, <see cref="WaterBlock.Body"/>) sit at the hydrostatic fill under it without
/// stepping. What they give to or take from free water is booked to the body's ledger; a <see cref="LevelStep"/> of it over
/// the body's area moves the level. The sea is body <see cref="Sea"/>, with infinite area, so its level never moves.
/// Blocks read <see cref="Levels"/> while they step; it is written only between steps.
/// </summary>
public sealed class WaterBodies
{
    public const ushort None = 0;
    public const ushort Sea = 1;
    public const int MaxBodies = 4096;

    /// <summary>The level moves in steps of this (m), once the ledger holds that much water over the body's area.</summary>
    public const float LevelStep = 0.01f;

    /// <summary>Free water whose surface is within this (m) of a body's level, and connected to it, is the body's (<see cref="WaterBlock.JoinBodies"/>).</summary>
    public const float JoinTolerance = 0.05f;

    /// <summary>Rain at full intensity falls at this rate (m/s): 15 mm/h, a downpour. A body gains it over its area.</summary>
    public const float RainRate = 15e-3f / 3600f;

    public struct Body
    {
        /// <summary>World y of the surface.</summary>
        public float Level;

        /// <summary>Surface area (m²); infinite for the sea.</summary>
        public float Area;

        /// <summary>Water (m³) the body has taken in (positive) or given (negative) since its level last moved.</summary>
        public float Ledger;

        /// <summary>Net water (m³) given away since the start: what the ledger has lowered it by. Diagnostics.</summary>
        public float Given;

        /// <summary>Where it is: a centre and radius (world x, z) for generated bodies, the mean of its cells for born ones.</summary>
        public Vector2 Centre;
        public float Radius;

        /// <summary>World y of its lowest cell's floor; the level can't go under it.</summary>
        public float Bottom;

        /// <summary>
        /// World y of its highest held cell's top; the level can't go over it (<see cref="SettleLevels"/>). Over it the held
        /// cells have no room for the water, which stood only for pressure: the crater lake booked itself 24 m over its
        /// shore, and wherever its blocks were awake its top cells pushed free water up to that level, a cylinder of water
        /// standing in the air.
        /// </summary>
        public float Top;

        /// <summary>The colour and fog density of its water (<see cref="WaterColour"/>), given to the cells it holds.</summary>
        public uint Colour;

        public bool Alive;

        /// <summary>Born of free water at rest in play (<see cref="WaterBlock.BornBodies"/>), not generated: released when it overflows (<see cref="SettleLevels"/>).</summary>
        public bool Born;
    }

    /// <summary>A table with the sea at level 0 for blocks built outside a system (tests, tools).</summary>
    public static readonly WaterBodies Default = new(0f);

    private readonly List<Body> _bodies = new(64);
    private readonly List<ushort> _moved = new(16);

    /// <summary>Per body id, its level (world y); NaN for none. Read by blocks as they step.</summary>
    public readonly float[] Levels = new float[MaxBodies];

    /// <summary>World y of the sea's top voxel layer: generated water at or under it is the sea's.</summary>
    public readonly int SeaTop;

    public WaterBodies(float seaLevel)
    {
        Levels.AsSpan().Fill(float.NaN);
        _bodies.Add(default);
        SeaTop = (int)MathF.Ceiling(seaLevel) - 1;
        Add(SeaTop + 0.5f, float.PositiveInfinity, Vector2.Zero, float.PositiveInfinity, float.NegativeInfinity, WaterColour.Sea);
    }

    public int Count => _bodies.Count;

    public ref Body this[ushort id] => ref System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_bodies)[id];

    /// <summary>Bodies whose level moved in the last <see cref="SettleLevels"/>.</summary>
    public IReadOnlyList<ushort> Moved => _moved;

    /// <summary>Born bodies the last <see cref="SettleLevels"/> released: their held cells go back to free water (<see cref="WaterBlock.ReleaseDead"/>).</summary>
    public IReadOnlyList<ushort> Released => _released;

    private readonly List<ushort> _released = new(4);

    /// <param name="colour">The water's colour and fog density (<see cref="WaterColour"/>); a born body takes its cells' own.</param>
    /// <param name="born">Born of free water in play (<see cref="Body.Born"/>), not generated.</param>
    public ushort Add(float level, float area, Vector2 centre, float radius, float bottom, uint colour = WaterColour.Lake, bool born = false)
    {
        if (_bodies.Count == MaxBodies)
            throw new InvalidOperationException("Too many bodies of water.");

        var id = (ushort)_bodies.Count;
        _bodies.Add(new Body { Level = level, Area = area, Centre = centre, Radius = radius, Bottom = bottom, Top = CellTop(level), Colour = colour, Alive = true, Born = born });
        Levels[id] = level;
        return id;
    }

    /// <summary>
    /// Fill of a held cell whose centre is at world y under a surface at <paramref name="level"/>: the part of it under
    /// the surface, and over full the pressure of the water above, <see cref="WaterFlow.Compression"/> per metre, the
    /// vertical equilibrium of both flow models. 0 above the surface.
    /// </summary>
    public static float Fill(int worldY, float level)
    {
        var over = level - (worldY + 0.5f);
        return over >= 0f ? 1f + WaterFlow.Compression * over : MathF.Max(0f, 1f + over);
    }

    /// <summary>The swell's pressure fades out of the sea's held cells over this depth (m) under its top layer.</summary>
    public const float SwellDepth = 8f;

    /// <summary>
    /// The sea's level over a held cell at world y: its rest level plus the swell's height there (<see cref="WaterBlock.Swell"/>),
    /// faded out over <see cref="SwellDepth"/> under the sea's top layer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SwellLevel(float rest, float swell, int worldY, int seaTop) =>
        rest + swell * Math.Clamp(1f - (seaTop - worldY) * (1f / SwellDepth), 0f, 1f);

    /// <summary>Books water a body's held cells took in (positive) or gave (negative) this step.</summary>
    public void Exchange(ushort id, float amount)
    {
        if (id <= Sea || amount == 0f)
            return;

        ref var body = ref this[id];
        body.Ledger += amount;
        body.Given -= amount;
    }

    /// <summary>Books the water a body paid to take cells in (<see cref="WaterBlock.JoinBodies"/>) and the surface area they add.</summary>
    /// <summary>The top (world y) of the cell whose volume holds a surface at <paramref name="level"/>: cell y spans [y − ½, y + ½].</summary>
    public static float CellTop(float level) => MathF.Floor(level + 0.5f) + 0.5f;

    /// <summary>The body holds a cell whose top is at world y <paramref name="top"/> (main thread).</summary>
    public void RaiseTop(ushort id, float top)
    {
        if (id <= Sea)
            return;

        ref var body = ref this[id];
        body.Top = MathF.Max(body.Top, top);
    }

    public void Took(ushort id, float paid, int surfaceCells)
    {
        if (id <= Sea)
            return;

        ref var body = ref this[id];
        body.Ledger -= paid;
        body.Given += paid;
        body.Area += surfaceCells;
    }

    /// <summary>
    /// Between steps: every body whose ledger holds a <see cref="LevelStep"/> over its area moves its level by what it
    /// holds, not under its bottom. The bodies moved are listed in <see cref="Moved"/> for their blocks to refill.
    /// </summary>
    public void SettleLevels()
    {
        _moved.Clear();
        _released.Clear();

        for (var id = Sea + 1; id < _bodies.Count; id++)
        {
            ref var body = ref this[(ushort)id];

            if (!body.Alive || body.Area <= 0f || MathF.Abs(body.Ledger) < body.Area * LevelStep)
                continue;

            // Not over its highest held cell's top: what more it took waits in the ledger until it gives it back.
            var target = body.Level + body.Ledger / body.Area;

            // A born body fed past its top is no still pool: water runs through it (it was born while a stream stood
            // still), and held at a fixed level it swallowed the stream on one side and poured it out on the other
            // without end. It goes back to free water. (What it took past its top, under a level step, goes with it.)
            if (body.Born && target > body.Top)
            {
                body.Alive = false;
                body.Ledger = 0f;
                _released.Add((ushort)id);
                continue;
            }

            var level = MathF.Max(body.Bottom, MathF.Min(target, MathF.Max(body.Top, body.Level)));

            if (level == body.Level)
                continue;

            body.Ledger = target > level ? (target - level) * body.Area : 0f;
            body.Level = level;
            Levels[id] = body.Level;
            _moved.Add((ushort)id);
        }
    }
}
