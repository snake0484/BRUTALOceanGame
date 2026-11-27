using BrutalGame.Water.Coast;

namespace BrutalGame.Water.Ocean;

/// <summary>
/// The breaker face judged in motion (breaking-waves-brief.md §8): at fixed rest points, tick after tick, how far its
/// height, normal and whitewater move, and how sharply that motion changes. A wave moves a point fast but smoothly; a
/// pop is one tick out of step with the ticks either side, so it shows in the second difference (the kick) far above a
/// wave's own, whatever the step. Accumulates over any number of <see cref="Run"/>s.
/// </summary>
public struct BreakerStability
{
    /// <summary>A step past these in one tick counts as a jump: height (m), the normal's turn (rad), whitewater weight.</summary>
    public const float HeightJump = 0.08f;
    public const float NormalJump = 0.35f;
    public const float FoamJump = 0.2f;

    /// <summary>A second difference past these counts as a kick: twice a breaking face's own at 60 Hz (up to 0.0075 m, 0.011 rad, 0.019).</summary>
    public const float HeightKick = 0.015f;
    public const float NormalKick = 0.03f;
    public const float FoamKick = 0.04f;

    public int Samples;
    public int HeightJumps;
    public int SlopeJumps;
    public int FoamJumps;
    public int Kicks;
    public float MaxHeightStep;
    public float MaxShiftStep;
    public float MaxNormalStep;
    public float MaxFoamStep;
    public float MaxHeightKick;
    public float MaxNormalKick;
    public float MaxFoamKick;
    public float MaxHeight;
    public float MaxFoam;

    public readonly int Jumps => HeightJumps + SlopeJumps + FoamJumps;

    public void Run(CoastAtlas atlas, float x, float z, long firstTick, int ticks, double tickSeconds, float spacing = 0f)
    {
        var weight = CoastWindow.Weight(x / CoastWindow.CellSize, z / CoastWindow.CellSize);
        var first = Face(atlas, x, z, firstTick * tickSeconds, weight, spacing);
        var before = first;

        for (var i = 1; i <= ticks; i++)
        {
            var face = Face(atlas, x, z, (firstTick + i) * tickSeconds, weight, spacing);
            Step(before, face);

            if (i > 1)
                Kick(first, before, face);

            first = before;
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

    /// <summary>The second difference of three ticks running at a rest point: how far the middle one is out of step.</summary>
    public void Kick(in FacePoint first, in FacePoint second, in FacePoint third)
    {
        Normal(first, out var ax, out var ay, out var az);
        Normal(second, out var bx, out var by, out var bz);
        Normal(third, out var cx, out var cy, out var cz);
        var (x, y, z) = (ax - 2f * bx + cx, ay - 2f * by + cy, az - 2f * bz + cz);
        var height = MathF.Abs(first.Height - 2f * second.Height + third.Height);
        var normal = MathF.Sqrt(x * x + y * y + z * z);
        var foam = MathF.Abs(first.Foam - 2f * second.Foam + third.Foam);

        Kicks += height > HeightKick || normal > NormalKick || foam > FoamKick ? 1 : 0;
        MaxHeightKick = MathF.Max(MaxHeightKick, height);
        MaxNormalKick = MathF.Max(MaxNormalKick, normal);
        MaxFoamKick = MathF.Max(MaxFoamKick, foam);
    }

    private static FacePoint Face(CoastAtlas atlas, float x, float z, double time, float weight, float spacing)
    {
        BreakerFace.Apply(atlas, x, z, atlas.PhaseAt(time), out var p, spacing);
        p.Height *= weight;
        p.SlopeX *= weight;
        p.SlopeZ *= weight;
        p.Dx *= weight;
        p.Dz *= weight;
        p.DxX *= weight;
        p.DzZ *= weight;
        p.DxZ *= weight;
        p.Foam *= weight;
        return p;
    }

    public override readonly string ToString() =>
        $"{Samples} samples: height step max {MaxHeightStep:F3} m ({HeightJumps} over {HeightJump}), shift step max {MaxShiftStep:F3} m, normal turn max {MaxNormalStep:F3} rad ({SlopeJumps} over {NormalJump}), foam step max {MaxFoamStep:F3} ({FoamJumps} over {FoamJump}); second differences max height {MaxHeightKick:F4} m, normal {MaxNormalKick:F4} rad, foam {MaxFoamKick:F4} ({Kicks} kicks); peak height {MaxHeight:F2} m, foam {MaxFoam:F2}";

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
