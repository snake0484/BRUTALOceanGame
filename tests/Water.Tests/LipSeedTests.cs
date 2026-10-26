using System.Numerics;
using BrutalGame.Water.Ocean;
using Xunit.Abstractions;

namespace BrutalGame.Water.Tests;

/// <summary>
/// The lips' seeds measured against the wave they sit on (docs/breaking-waves.md, eighteenth pass). The carrier says
/// where a lip is: curl, height and reach over the lip thresholds at a crest point (BreakerLip.Sample's gate). The
/// segments BreakerFaces builds are what the ribbon is swept along. Along rows across the surf zone, every crest point
/// the carrier puts a lip on, every tick: is a segment under it at full taper, and does that answer hold from one tick
/// to the next while the carrier's does? A miss is a lip with no ribbon; a flip is a ribbon that comes or goes on a crest
/// whose wave did not change. Both seedings are measured on the same ticks, the chained crest polylines and the raw
/// marching-squares crossings, on a beach whose shoreline bows and carries a skewed bar with a trough behind it, where
/// chains split at holes in the surf mask and are resampled as their length changes.
/// </summary>
public sealed class LipSeedTests(ITestOutputHelper output)
{
    private const int Warmup = 600;
    private const int Ticks = 480;
    private const float RowSpacing = 16f;
    private const float Step = 4f;
    private const float From = 360f;
    private const float To = 900f;

    /// <summary>A segment this close to the crest point covers it, at this much of its end taper.</summary>
    private const float Cover = 1f;
    private const float MinTaper = 0.5f;

    /// <summary>A crest point within this of last tick's on the same row is the same crest (crests move under 0.2 m a tick and lie tens of metres apart).</summary>
    private const float Track = 1f;

    /// <summary>The camera, on the bar: segments are built within BreakerFaces.LipDistance of it, as in the game, and only crest points within that range count.</summary>
    private static readonly Vector2 Focus = new(640f, 512f);

    private readonly record struct Crest(float X, bool Lip, bool Chains, bool Raw, float MissChains, float MissRaw);

    private sealed class Tally
    {
        public int Lips;
        public int Misses;
        public int Pairs;
        public int Flips;
        public float WorstMiss;
        public long Segments;
        public readonly List<string> Notes = new();

        public void Count(bool covered, float miss, string where)
        {
            Lips++;
            if (covered)
                return;

            Misses++;
            WorstMiss = MathF.Max(WorstMiss, miss);
            Note($"miss {where}, nearest segment {miss:F2} m");
        }

        public void Flip(bool before, bool now, string where)
        {
            Pairs++;
            if (before == now)
                return;

            Flips++;
            Note($"flip {where}: {(now ? "appeared" : "vanished")}");
        }

        private void Note(string note)
        {
            if (Notes.Count < 12)
                Notes.Add(note);
        }
    }

    [Fact]
    public void RawCrossings_CoverEveryLip_AndNeverFlicker()
    {
        var swell = BreakerSetTests.BarBeachForProbe(0.15f);
        var set = new BreakerSet();
        var chains = new BreakerFaces { UseChains = true };
        var raw = new BreakerFaces();
        var chainTally = new Tally();
        var rawTally = new Tally();
        var rows = (int)((CoastalField.Resolution - 4) * CoastalField.CellSize / RowSpacing);
        var previous = new List<Crest>[rows];
        var current = new List<Crest>[rows];

        for (var r = 0; r < rows; r++)
        {
            previous[r] = new List<Crest>();
            current[r] = new List<Crest>();
        }

        for (var t = 0; t < Warmup + Ticks; t++)
        {
            swell.SetTime(100.0 + t / 60.0);
            set.Update(swell, 1f / 60f);
            if (t < Warmup - 1)
                continue;

            chains.Build(set, swell, Focus, BreakerFaces.LipDistance);
            raw.Build(set, swell, Focus, BreakerFaces.LipDistance);
            chainTally.Segments += chains.SegmentCount;
            rawTally.Segments += raw.SegmentCount;

            for (var r = 0; r < rows; r++)
            {
                var z = 2f * CoastalField.CellSize + (r + 0.5f) * RowSpacing;
                Scan(swell, set, chains, raw, z, current[r]);
                if (t >= Warmup)
                    Compare(previous[r], current[r], chainTally, rawTally, t - Warmup, z);

                (previous[r], current[r]) = (current[r], previous[r]);
            }
        }

        Report("chained polylines", chainTally);
        Report("raw crossings", rawTally);
        foreach (var note in chainTally.Notes)
            output.WriteLine($"  chains: {note}");
        foreach (var note in rawTally.Notes)
            output.WriteLine($"  raw: {note}");

        Assert.True(rawTally.Lips > 500, $"only {rawTally.Lips} lip samples: the fixture breaks too little");
        Assert.True(rawTally.Misses <= 0.01f * rawTally.Lips, $"{rawTally.Misses} of {rawTally.Lips} lips have no segment under them");
        Assert.True(rawTally.Flips <= 0.001f * rawTally.Pairs, $"{rawTally.Flips} coverage flips over {rawTally.Pairs} tick pairs");
        Assert.True(rawTally.Misses <= chainTally.Misses && rawTally.Flips <= chainTally.Flips, "the raw crossings cover lips worse than the chains did");
    }

    private void Report(string seeds, Tally tally)
    {
        output.WriteLine($"{seeds}: {tally.Segments / (Ticks + 1f):F0} segments a tick; {tally.Lips} lip samples, {tally.Misses} uncovered ({100f * tally.Misses / Math.Max(tally.Lips, 1):F2}%, nearest segment up to {tally.WorstMiss:F2} m away); {tally.Pairs} tick pairs, {tally.Flips} coverage flips ({1000f * tally.Flips / Math.Max(tally.Pairs, 1):F2} per 1000)");
    }

    /// <summary>The carrier's crest points along the row at z, each with whether the carrier puts a lip there and whether each seeding covers it.</summary>
    private static void Scan(OceanSwell swell, BreakerSet set, BreakerFaces chains, BreakerFaces raw, float z, List<Crest> crests)
    {
        crests.Clear();
        var last = float.NaN;

        for (var x = From; x <= To; x += Step)
        {
            if (!swell.SurfCarrier(x, z, out var c))
            {
                last = float.NaN;
                continue;
            }

            // The wrapped phase rises through 0 at a crest (and jumps from +π to −π at a trough).
            var phase = Wrap(c.Phase);
            var crossed = last < 0f && phase >= 0f && phase - last < MathF.PI;
            last = phase;
            if (!crossed || !Seat(swell, x - 0.5f * Step, z, out var q) || Vector2.Distance(q, Focus) > BreakerFaces.LipDistance)
                continue;

            var coveredChains = Covered(chains, q, out var missChains);
            var coveredRaw = Covered(raw, q, out var missRaw);
            crests.Add(new Crest(q.X, LipAt(swell, set, q), coveredChains, coveredRaw, missChains, missRaw));
        }
    }

    private static void Compare(List<Crest> previous, List<Crest> current, Tally chains, Tally raw, int tick, float z)
    {
        foreach (var now in current)
        {
            if (!now.Lip)
                continue;

            var where = $"tick {tick} at x {now.X:F1} z {z:F0}";
            chains.Count(now.Chains, now.MissChains, where);
            raw.Count(now.Raw, now.MissRaw, where);

            foreach (var before in previous)
            {
                if (!before.Lip || MathF.Abs(before.X - now.X) > Track)
                    continue;

                chains.Flip(before.Chains, now.Chains, where);
                raw.Flip(before.Raw, now.Raw, where);
                break;
            }
        }
    }

    /// <summary>Two Newton steps along the wavevector to the crest nearest (x, z), as the lip bases seat (seatOnCrest).</summary>
    private static bool Seat(OceanSwell swell, float x, float z, out Vector2 q)
    {
        q = new Vector2(x, z);

        for (var i = 0; i < 2; i++)
        {
            if (!swell.SurfCarrier(q.X, q.Y, out var c))
                return false;

            q -= new Vector2(c.Kx, c.Kz) * (Wrap(c.Phase) / (c.Kx * c.Kx + c.Kz * c.Kz));
        }

        return true;
    }

    /// <summary>Whether the carrier puts a lip on the crest at q: BreakerLip.Sample's gate, curl, height and reach over the lip thresholds.</summary>
    private static bool LipAt(OceanSwell swell, BreakerSet set, Vector2 q)
    {
        if (!swell.SurfCarrier(q.X, q.Y, out var c))
            return false;

        var state = BreakerFace.CrestStateAt(swell, set, q.X, q.Y);
        if (state.Curl < 0.05f || state.Height < 0.05f)
            return false;

        var k = MathF.Max(MathF.Sqrt(c.Kx * c.Kx + c.Kz * c.Kz), 1e-6f);
        var breakDistance = set.BreakDistance(q.X, q.Y);
        var plunge = BreakerFace.PlungeAt(swell, q, new Vector2(c.Kx, c.Kz) / k, c.Depth, breakDistance, c.Unbroken);
        return BreakerLip.Life(breakDistance, MathF.Tau / k, plunge).Reach >= BreakerLip.MinReach;
    }

    /// <summary>A segment within <see cref="Cover"/> of q whose end taper there is at least <see cref="MinTaper"/>; miss = the nearest segment's distance.</summary>
    private static bool Covered(BreakerFaces faces, Vector2 q, out float miss)
    {
        miss = float.MaxValue;
        var covered = false;

        for (var i = 0; i < faces.SegmentCount; i++)
        {
            ref readonly var s = ref faces.Segments[i];
            var a = new Vector2(s.Ends.X, s.Ends.Y);
            var ab = new Vector2(s.Ends.Z, s.Ends.W) - a;
            var t = Math.Clamp(Vector2.Dot(q - a, ab) / MathF.Max(ab.LengthSquared(), 1e-6f), 0f, 1f);
            var d = Vector2.Distance(q, a + ab * t);
            miss = MathF.Min(miss, d);
            covered |= d <= Cover && float.Lerp(s.Heights.Z, s.Heights.W, t) >= MinTaper;
        }

        return covered;
    }

    private static float Wrap(float phase) => phase - MathF.Tau * MathF.Floor(phase / MathF.Tau + 0.5f);
}
