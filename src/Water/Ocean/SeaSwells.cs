using System.Diagnostics;
using Brutal.Numerics;

namespace BrutalGame.Water.Ocean;

/// <summary>
/// The sea's swells. Pinned (no <see cref="ISeaWind"/>, or <see cref="Driven"/> off), one <see cref="Uniform"/> swell is
/// laid on every tile. Driven, each <see cref="SeaTiles"/> tile has its own swell from its wind schedule: at each epoch's
/// first tick the tile's key is read, and a new key crossfades in from that tick over <see cref="BlendShare"/> of the
/// epoch, its components drawn from the tile and the key. A tile's swell is then a function of the seed, the tile and
/// the tick alone, so one made late (an anchor arriving) agrees with one that has run all along, and every peer agrees.
/// Only the tiles round the anchors are kept live (<see cref="Tick"/>); a query elsewhere finds no swell.
/// Main thread only for <see cref="Tick"/> and the setters; <see cref="For"/> and the swells are read-only between ticks.
/// </summary>
public sealed unsafe class SeaSwells
{
    public const int InitialCapacity = 16;

    /// <summary>A tile is dropped once no anchor has wanted it for this long (ticks).</summary>
    public const long KeepTicks = 600;

    public const double BlendShare = 0.75;

    public readonly SeaTiles Tiles;
    public readonly OceanSwell Uniform;

    private readonly ulong _seed;
    private readonly double _tickSeconds;
    private readonly ISeaWind? _wind;
    private readonly int[] _slotOf;
    private SwellParams _base = new();
    private bool _driven;
    private Slot[] _slots = new Slot[InitialCapacity];
    private int _count;
    private int _misses;

    private struct Slot
    {
        public OceanSwell Swell;
        public int Tile;
        public SeaWindKey Key;
        public long Epoch;
        public long Wanted;

        /// <summary>The next epoch's key, read ahead (<see cref="PrefetchTick"/>), and that epoch.</summary>
        public SeaWindKey Next;
        public long NextEpoch;
    }

    public SeaSwells(ulong seed, SeaTiles tiles, double tickSeconds, ISeaWind? wind = null)
        : this(new OceanSwell(seed), tiles, tickSeconds, wind, seed)
    {
    }

    /// <summary>Pinned to <paramref name="uniform"/>, which the caller may also move itself (tests, tools).</summary>
    public SeaSwells(OceanSwell uniform, SeaTiles tiles, double tickSeconds)
        : this(uniform, tiles, tickSeconds, null, 0UL)
    {
    }

    private SeaSwells(OceanSwell uniform, SeaTiles tiles, double tickSeconds, ISeaWind? wind, ulong seed)
    {
        _seed = seed;
        Tiles = tiles;
        _tickSeconds = tickSeconds;
        _wind = wind;
        _slotOf = new int[tiles.Count];
        Uniform = uniform;
    }

    /// <summary>Each tile follows its wind schedule; off (or with none) the <see cref="Uniform"/> swell is everywhere.</summary>
    public bool Driven => _driven;

    /// <summary>Swell time (s): of the last <see cref="Tick"/>, or the uniform swell's.</summary>
    public double Time => _driven ? _time : Uniform.Time;

    private double _time;

    public long LastTick { get; private set; }

    public int LiveCount => _driven ? _count : 1;

    /// <summary>Queries since the last tick that fell on a tile with no live swell (and saw a calm sea).</summary>
    public int Misses => _misses;

    public float LastTickMs { get; private set; }

    /// <summary>Tile swells made, and keys read, in the last tick.</summary>
    public int LastMade { get; private set; }
    public int LastKeys { get; private set; }

    /// <summary>The live swell of a tile and its key; for the panel.</summary>
    public int Live(int index, out int tile, out SeaWindKey key, out OceanSwell swell)
    {
        ref readonly var slot = ref _slots[index];
        (tile, key, swell) = (slot.Tile, slot.Key, slot.Swell);
        return index;
    }

    /// <summary>
    /// The groundswell and spectrum settings every tile shares; pinned, its wind too. Driven, each tile takes its own
    /// key's wind (and keeps this direction when calm). Every live tile swell is made again.
    /// </summary>
    public void SetBase(in SwellParams swell, bool driven)
    {
        _base = swell;
        _driven = driven && _wind is not null;

        for (var i = 0; i < _count; i++)
            _slotOf[_slots[i].Tile] = 0;

        _count = 0;

        if (!_driven)
            Uniform.SetWind(swell);
    }

    /// <summary>The settings <see cref="SetBase"/> took.</summary>
    public SwellParams Base => _base;

    /// <summary>The tile a planet-frame point is in (the first of its blend).</summary>
    public int TileAt(in double3 planetLocal)
    {
        Tiles.Locate(double3.Normalize(planetLocal), out var blend);
        return blend.Tile[0];
    }

    /// <summary>The swell drawn about a point: its tile's, else the uniform one (for the GPU and readouts, not for queries).</summary>
    public OceanSwell Near(in double3 planetLocal) => For(TileAt(planetLocal)) ?? Uniform;

    /// <summary>
    /// The GPU detail's spectrum about a point: its blend's live tiles' spectra mixed by their squared weights (wind
    /// direction as a vector), so the FFT detail changes smoothly across a tile edge as the swell does.
    /// </summary>
    public SpectrumState SpectrumAt(in double3 planetLocal)
    {
        Tiles.Locate(double3.Normalize(planetLocal), out var blend);

        if (!_driven)
            return Uniform.GpuSpectrum;

        var total = 0f;
        var p = new SwellParams();
        (p.WindSpeed, p.Fetch, p.Gamma, p.Spread, p.MinWavelength, p.Choppiness) = (0f, 0f, 0f, 0f, 0f, 0f);
        var depth = 0f;
        var finite = true;
        var dx = 0f;
        var dz = 0f;
        OceanSwell? only = null;

        for (var k = 0; k < blend.Count; k++)
        {
            if (For(blend.Tile[k]) is not { } swell)
                continue;

            var w = blend.Weight[k] * blend.Weight[k];
            var q = swell.GpuSpectrum.Params;
            only = total == 0f ? swell : null;
            total += w;
            p.WindSpeed += w * q.WindSpeed;
            p.Fetch += w * q.Fetch;
            p.Gamma += w * q.Gamma;
            p.Spread += w * q.Spread;
            p.MinWavelength += w * q.MinWavelength;
            p.Choppiness += w * q.Choppiness;
            depth += w * q.Depth;
            finite &= float.IsFinite(q.Depth);
            var (sin, cos) = MathF.SinCos(q.WindDirection);
            dx += w * cos;
            dz += w * sin;
        }

        if (only is not null || total <= 0f)
            return (only ?? Uniform).GpuSpectrum;

        var scale = 1f / total;
        p.WindSpeed *= scale;
        p.Fetch *= scale;
        p.Gamma *= scale;
        p.Spread *= scale;
        p.MinWavelength *= scale;
        p.Choppiness *= scale;
        p.Depth = finite ? depth * scale : float.PositiveInfinity;
        p.WindDirection = MathF.Atan2(dz, dx);
        return OceanSwell.Spectrum(p);
    }

    /// <summary>The swell on a tile, or null where none is live.</summary>
    public OceanSwell? For(int tile)
    {
        if (!_driven)
            return Uniform;

        var slot = _slotOf[tile];
        return slot > 0 ? _slots[slot - 1].Swell : null;
    }

    internal void Miss() => Interlocked.Increment(ref _misses);

    /// <summary>
    /// Moves every swell to <paramref name="tick"/> (the state the tick ends on), keeping live the tiles round each anchor
    /// (planet frame): the GPU's four round the nearest tile corner and the blend's. Values never depend on the anchors.
    /// </summary>
    public void Tick(long tick, ReadOnlySpan<double3> anchors)
    {
        var start = Stopwatch.GetTimestamp();
        LastTick = tick;
        _time = tick * _tickSeconds;
        _misses = 0;
        LastMade = 0;
        LastKeys = 0;

        if (!_driven)
        {
            Uniform.SetTime(_time);
            LastTickMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            return;
        }

        foreach (var anchor in anchors)
        {
            var dir = double3.Normalize(anchor);
            Tiles.Window(dir, out var window);
            Tiles.Locate(dir, out var blend);

            for (var k = 0; k < SeaTiles.MaxBlend; k++)
                Want(window.Tile[k], tick);

            for (var k = 0; k < blend.Count; k++)
                Want(blend.Tile[k], tick);
        }

        for (var i = 0; i < _count; i++)
        {
            ref var slot = ref _slots[i];

            if (tick - slot.Wanted > KeepTicks)
            {
                Drop(i--);
                continue;
            }

            Advance(ref slot, tick);
        }

        LastTickMs = (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private void Want(int tile, long tick)
    {
        var index = _slotOf[tile];

        if (index > 0)
        {
            _slots[index - 1].Wanted = tick;
            return;
        }

        if (_count == _slots.Length)
            Array.Resize(ref _slots, 2 * _slots.Length);

        ref var slot = ref _slots[_count++];
        _slotOf[tile] = _count;
        slot.Swell ??= new OceanSwell(_seed);
        slot.Swell.BlendSeconds = BlendShare * SeaWind.EpochTicks * _tickSeconds;
        slot.Tile = tile;
        slot.Wanted = tick;
        slot.NextEpoch = long.MinValue;
        Make(ref slot, tick);
    }

    /// <summary>The tile's swell as it stands at <paramref name="tick"/>: last epoch's key settled, this epoch's blending in.</summary>
    private void Make(ref Slot slot, long tick)
    {
        var epoch = SeaWind.Epoch(tick);
        var before = _wind!.At(slot.Tile, epoch - 1);
        slot.NextEpoch = long.MinValue;
        slot.Swell.Reset();
        slot.Swell.SetTime(EpochStart(epoch));
        slot.Swell.SetWind(Params(before), SetSeed(slot.Tile, before), EpochStart(epoch - 1));
        slot.Key = before;
        slot.Epoch = epoch - 1;
        LastMade++;
        LastKeys++;
        Advance(ref slot, tick);
    }

    private void Advance(ref Slot slot, long tick)
    {
        var epoch = SeaWind.Epoch(tick);

        if (epoch != slot.Epoch)
        {
            if (epoch != slot.Epoch + 1)
            {
                Make(ref slot, tick);
                return;
            }

            var key = slot.NextEpoch == epoch ? slot.Next : _wind!.At(slot.Tile, epoch);
            LastKeys += slot.NextEpoch == epoch ? 0 : 1;
            slot.Epoch = epoch;

            if (key != slot.Key)
            {
                slot.Key = key;
                slot.Swell.SetWind(Params(key), SetSeed(slot.Tile, key), EpochStart(epoch));
            }
        }

        if (slot.NextEpoch != epoch + 1 && tick - epoch * SeaWind.EpochTicks >= PrefetchTick(slot.Tile))
        {
            slot.Next = _wind!.At(slot.Tile, epoch + 1);
            slot.NextEpoch = epoch + 1;
            LastKeys++;

            if (slot.Next != slot.Key)
                slot.Swell.Prebuild(Params(slot.Next), SetSeed(slot.Tile, slot.Next));
        }

        slot.Swell.SetTime(_time);
    }

    /// <summary>
    /// When in its epoch a tile reads the next epoch's wind and builds its set, so epoch ticks only swap sets in: spread
    /// over the epoch's middle by tile, after this epoch's own blend has begun.
    /// </summary>
    private static long PrefetchTick(int tile) => 600 + tile * 61L % (SeaWind.EpochTicks - 1200);

    private void Drop(int index)
    {
        _slotOf[_slots[index].Tile] = 0;
        _count--;

        if (index == _count)
            return;

        (_slots[index], _slots[_count]) = (_slots[_count], _slots[index]);
        _slotOf[_slots[index].Tile] = index + 1;
    }

    private double EpochStart(long epoch) => epoch * SeaWind.EpochTicks * _tickSeconds;

    private SwellParams Params(SeaWindKey key) => _base with
    {
        WindSpeed = key.WindSpeed,
        WindDirection = key.Speed > 0 ? key.WindDirection : _base.WindDirection,
    };

    private ulong SetSeed(int tile, SeaWindKey key) =>
        SplitMix64.Mix(SplitMix64.Mix(_seed, (ulong)tile), (ulong)(ushort)key.Speed << 16 | (ushort)key.Direction);
}
