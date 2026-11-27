using System.Runtime.CompilerServices;

namespace BrutalGame.Water.Coast;

/// <summary>A bilinear sample of a <see cref="CoastAtlas"/>.</summary>
public struct CoastSample
{
    public float Phase;
    public float Depth;
    public float Amplitude;
    public float BreakDistance;
    public float Plunge;
    public float DirX;
    public float DirZ;
}

/// <summary>
/// One coast region's baked map (<see cref="CoastBake"/>): per cell of its <see cref="CoastWindow"/>, the carrier's
/// spatial phase (float: half floats lose it), then as half floats the water depth, the shoaling amplitude factor, the
/// travel since the wave began to break and its breaker type, and the direction of travel as two signed bytes. 14 bytes
/// a cell, <see cref="Bytes"/> a region. The layout is the GPU's (CoastAtlasGpu): three textures.
/// </summary>
public sealed class CoastAtlas
{
    public const int N = CoastWindow.Cells;
    public const int FieldChannels = 4;
    public const int DepthChannel = 0;
    public const int AmplitudeChannel = 1;
    public const int BreakChannel = 2;
    public const int PlungeChannel = 3;
    public const int Bytes = N * N * (sizeof(float) + FieldChannels * 2 + 2);
    public const float MinDepth = -20f;
    public const float MaxDepth = 1000f;
    public const float MaxBreakDistance = 1000f;
    private const float DirectionScale = 1f / 127f;

    public readonly float[] Phase = new float[N * N];
    public readonly Half[] Fields = new Half[N * N * FieldChannels];
    public readonly sbyte[] Direction = new sbyte[N * N * 2];

    public CoastRegionKey Key;

    /// <summary>The carrier's deep-water height the map is drawn with (m): its key's when baked, then the swell's while it stays within the key's tolerance.</summary>
    public float Height;

    /// <summary>The window's corner in its tile's plane (m).</summary>
    public double OriginU;
    public double OriginV;

    /// <summary>
    /// The map's phase at <paramref name="time"/> (s), wrapped: the carrier's phase at local (x, z) is the cell's
    /// spatial phase plus this. The spatial phase is local to the window, so the corner's part is added here in double.
    /// </summary>
    public float PhaseAt(double time)
    {
        var carrier = Key.Carrier;
        var (sin, cos) = Math.SinCos((double)carrier.Direction);
        var omega = (double)carrier.Omega;
        var phase = omega * omega / 9.81 * (cos * OriginU + sin * OriginV) - omega * time;
        return (float)(phase - Math.Floor(phase / Math.Tau) * Math.Tau);
    }

    /// <summary>Bilinear at local (x, z) metres, clamped to the window (the GPU samplers clamp to edge).</summary>
    public void Sample(float x, float z, out CoastSample s)
    {
        var fx = Math.Clamp(x / CoastWindow.CellSize - 0.5f, 0f, N - 1.001f);
        var fz = Math.Clamp(z / CoastWindow.CellSize - 0.5f, 0f, N - 1.001f);
        var ix = (int)fx;
        var iz = (int)fz;
        var tx = fx - ix;
        var tz = fz - iz;
        var c00 = ix + iz * N;
        var c10 = c00 + 1;
        var c01 = c00 + N;
        var c11 = c01 + 1;

        s.Phase = Lerp2(Phase[c00], Phase[c10], Phase[c01], Phase[c11], tx, tz);
        s.Depth = Field(c00, c10, c01, c11, DepthChannel, tx, tz);
        s.Amplitude = Field(c00, c10, c01, c11, AmplitudeChannel, tx, tz);
        s.BreakDistance = Field(c00, c10, c01, c11, BreakChannel, tx, tz);
        s.Plunge = Field(c00, c10, c01, c11, PlungeChannel, tx, tz);
        s.DirX = DirectionScale * Lerp2(Direction[2 * c00], Direction[2 * c10], Direction[2 * c01], Direction[2 * c11], tx, tz);
        s.DirZ = DirectionScale * Lerp2(Direction[2 * c00 + 1], Direction[2 * c10 + 1], Direction[2 * c01 + 1], Direction[2 * c11 + 1], tx, tz);
    }

    internal void Write(int cell, float phase, float depth, float amplitude, float breakDistance, float plunge, float dirX, float dirZ)
    {
        Phase[cell] = phase;
        Fields[cell * FieldChannels + DepthChannel] = (Half)Math.Clamp(depth, MinDepth, MaxDepth);
        Fields[cell * FieldChannels + AmplitudeChannel] = (Half)amplitude;
        Fields[cell * FieldChannels + BreakChannel] = (Half)Math.Clamp(breakDistance, 0f, MaxBreakDistance);
        Fields[cell * FieldChannels + PlungeChannel] = (Half)plunge;
        Direction[2 * cell] = (sbyte)MathF.Round(Math.Clamp(dirX, -1f, 1f) * 127f);
        Direction[2 * cell + 1] = (sbyte)MathF.Round(Math.Clamp(dirZ, -1f, 1f) * 127f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float Field(int c00, int c10, int c01, int c11, int channel, float tx, float tz) => Lerp2(
        (float)Fields[c00 * FieldChannels + channel], (float)Fields[c10 * FieldChannels + channel],
        (float)Fields[c01 * FieldChannels + channel], (float)Fields[c11 * FieldChannels + channel], tx, tz);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Lerp2(float a, float b, float c, float d, float tx, float tz) =>
        float.Lerp(float.Lerp(a, b, tx), float.Lerp(c, d, tx), tz);
}
