namespace BrutalGame.Terrain;

internal static partial class LayerOps
{
    /// <summary>One unwarped octave over a row with vector lanes; false if the CPU lacks them (the caller runs the scalar loop).</summary>
    private static bool NoiseRowSimd(ReadOnlySpan<double> x, ReadOnlySpan<double> y, ReadOnlySpan<double> z, double f, double ox, double oy, double oz, uint seed, Span<float> n) => false;
}
