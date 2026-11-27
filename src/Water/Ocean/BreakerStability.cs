using BrutalGame.Water.Coast;

namespace BrutalGame.Water.Ocean;

/// <summary>
/// The breaker face judged in motion (breaking-waves-brief.md §8): at fixed rest points, tick after tick, how far its
/// height, normal and whitewater move, and how often by more than a wave's own travel explains. Accumulates over any
/// number of <see cref="Run"/>s.
/// </summary>
public struct BreakerStability
{
    /// <summary>A step past these in one tick counts as a jump: height (m), the normal's turn (rad), whitewater weight.</summary>
    public const float HeightJump = 0.08f;
    public const float NormalJump = 0.35f;
    public const float FoamJump = 0.2f;

    public int Samples;
    public int HeightJumps;
    public int SlopeJumps;
    public int FoamJumps;
    public float MaxHeightStep;
    public float MaxShiftStep;
    public float MaxNormalStep;
    public float MaxFoamStep;
    public float MaxHeight;
    public float MaxFoam;

    public void Run(CoastAtlas atlas, float x, float z, long firstTick, int ticks, double tickSeconds)
    {
        var weight = CoastWindow.Weight(x / CoastWindow.CellSize, z / CoastWindow.CellSize);
        BreakerFace.Apply(atlas, x, z, atlas.PhaseAt(firstTick * tickSeconds), out var before);
        Scale(ref before, weight);

        for (var i = 1; i <= ticks; i++)
        {
            BreakerFace.Apply(atlas, x, z, atlas.PhaseAt((firstTick + i) * tickSeconds), out var face);
            Scale(ref face, weight);
            Step(before, face);
            before = face;
        }
    }

    /// <summary>One tick's change of the drawn face at a rest point.</summary>
    public void Step(in FacePoint before, in FacePoint face)
    {
        Normal(before, out var bx, out var by, out var bz);
        Normal(face, out var nx, out var ny, out var nz);
        var height = MathF.Abs(face.Height - before.Height);
        var shift = MathF.Sqrt((face.Dx - before.Dx) * (face.Dx - before.Dx) + (face.Dz - before.Dz) * (face.Dz - before.Dz));
        var turn = MathF.Acos(Math.Clamp(nx * bx + ny * by + nz * bz, -1f, 1f));
        var foam = MathF.Abs(face.Foam - before.Foam);

        Samples++;
        HeightJumps += height > HeightJump ? 1 : 0;
        SlopeJumps += turn > NormalJump ? 1 : 0;
        FoamJumps += foam > FoamJump ? 1 : 0;
        MaxHeightStep = MathF.Max(MaxHeightStep, height);
        MaxShiftStep = MathF.Max(MaxShiftStep, shift);
        MaxNormalStep = MathF.Max(MaxNormalStep, turn);
        MaxFoamStep = MathF.Max(MaxFoamStep, foam);
        MaxHeight = MathF.Max(MaxHeight, face.Height);
        MaxFoam = MathF.Max(MaxFoam, face.Foam);
    }

    private static void Scale(ref FacePoint p, float weight)
    {
        p.Height *= weight;
        p.SlopeX *= weight;
        p.SlopeZ *= weight;
        p.Dx *= weight;
        p.Dz *= weight;
        p.DxX *= weight;
        p.DzZ *= weight;
        p.DxZ *= weight;
        p.Foam *= weight;
    }

    public override readonly string ToString() =>
        $"{Samples} samples: height step max {MaxHeightStep:F3} m ({HeightJumps} over {HeightJump}), shift step max {MaxShiftStep:F3} m, normal turn max {MaxNormalStep:F3} rad ({SlopeJumps} over {NormalJump}), foam step max {MaxFoamStep:F3} ({FoamJumps} over {FoamJump}); peak height {MaxHeight:F2} m, foam {MaxFoam:F2}";

    /// <summary>The displaced surface's normal from the face alone (ocean.frag's contract form).</summary>
    private static void Normal(in FacePoint p, out float x, out float y, out float z)
    {
        var dxx = 1f + p.DxX;
        var dzz = 1f + p.DzZ;
        x = p.SlopeZ * p.DxZ - dzz * p.SlopeX;
        y = dzz * dxx - p.DxZ * p.DxZ;
        z = p.DxZ * p.SlopeX - p.SlopeZ * dxx;
        var length = MathF.Max(MathF.Sqrt(x * x + y * y + z * z), 1e-6f);
        (x, y, z) = (x / length, y / length, z / length);
    }
}
