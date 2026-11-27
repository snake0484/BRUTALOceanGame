namespace BrutalGame.Weather;

/// <summary>
/// A weather system's kind: lows and storms bring cloud and rain and turn cyclonically; highs clear the sky, calm the
/// wind and turn the other way.
/// </summary>
public enum PressureKind : byte
{
    High,
    Low,
    Storm,
}
