namespace BrutalGame.Water.Ocean;

/// <summary>
/// A sea tile's wind over one epoch, quantized so a steady wind never rebuilds the sea: speed in whole m/s, direction in
/// <see cref="SeaWind.DirectionStep"/> steps of the tile's plane (from east toward south). Calm has direction 0.
/// </summary>
public readonly record struct SeaWindKey(short Speed, short Direction)
{
    public float WindSpeed => Speed;

    public float WindDirection => Direction * SeaWind.DirectionStep;

    public static SeaWindKey Of(float speed, float direction)
    {
        var s = (short)Math.Clamp(MathF.Round(speed), 0f, short.MaxValue);

        if (s == 0)
            return new SeaWindKey(0, 0);

        var d = (int)MathF.Round(direction / SeaWind.DirectionStep) % SeaWind.Directions;
        return new SeaWindKey(s, (short)(d < 0 ? d + SeaWind.Directions : d));
    }
}

/// <summary>Where each sea tile's wind comes from: a pure function of the tile and the epoch, so every peer agrees.</summary>
public interface ISeaWind
{
    SeaWindKey At(int tile, long epoch);
}

public static class SeaWind
{
    /// <summary>Ticks per epoch (60 s at 60 Hz): a tile's wind is sampled at each epoch's first tick.</summary>
    public const long EpochTicks = 3600;

    /// <summary>A new key's spectrum crossfades in over this share of its epoch.</summary>
    public const double BlendShare = 0.5;

    public const int Directions = 36;
    public const float DirectionStep = MathF.Tau / Directions;

    public static long Epoch(long tick) => tick >= 0 ? tick / EpochTicks : (tick - EpochTicks + 1) / EpochTicks;
}
