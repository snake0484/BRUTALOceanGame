using System.Numerics;

namespace BrutalGame.Weather;

public enum PressureKind : byte
{
    High,
    Low,
    Storm,
}

/// <summary>
/// One weather system at one instant: where it is, how far it reaches, and its peaks scaled by its life envelope.
/// Lows and storms bring cloud and rain and turn cyclonically; highs clear the sky, calm the wind and turn the other way.
/// </summary>
public struct PressureSystem
{
    public Vector2 Centre;
    public float Radius;
    public PressureKind Kind;

    /// <summary>Life envelope, 0..1: grows, matures, decays.</summary>
    public float Strength;

    /// <summary>Pressure anomaly at the centre (hPa) at full strength.</summary>
    public float Pressure;

    /// <summary>Strongest wind of the system's own circulation (m/s), on a ring at 0.45 × radius.</summary>
    public float PeakWind;

    /// <summary>Cloud it brings (lows) or clears (highs) at the centre, 0..1.</summary>
    public float Cloud;

    public float Rain;
    public float Convection;

    /// <summary>Share of the prevailing wind a high stills at its centre.</summary>
    public float Calm;
}
