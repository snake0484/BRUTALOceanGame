using Brutal.Numerics;

namespace BrutalGame.Weather;

/// <summary>
/// Per-body inputs to <see cref="SphereWeatherField"/>. The zonal steering wind alternates easterly and westerly over
/// <see cref="ZonalCells"/> bands per hemisphere (trades, westerlies, polar easterlies for three); the scales stretch the
/// island's pressure-system table to other bodies, so a gas giant is a parameter set, not a separate model.
/// </summary>
public struct PlanetClimate()
{
    public const double EarthRotationSpeed = 7.2921e-5 * 6.371e6;

    public ulong Seed = 1;

    /// <summary>Sea-level radius (m).</summary>
    public double Radius = 1_000_000.0;

    /// <summary>North pole in the planet frame; the body spins right-handed about it.</summary>
    public double3 SpinAxis = double3.UnitY;

    /// <summary>Circulation cells per hemisphere. Even bands (from the equator) blow easterly, odd ones westerly.</summary>
    public int ZonalCells = 3;

    /// <summary>Peak speed of the easterly bands (m/s), mid-band.</summary>
    public float EasterlySpeed = 6f;

    /// <summary>Peak speed of the westerly bands (m/s), mid-band.</summary>
    public float WesterlySpeed = 8f;

    /// <summary>0..1: each band's jet is scaled by 1 ± this, from the seed.</summary>
    public float JetVariation = 0.2f;

    /// <summary>Poleward wind per unit of zonal wind: equatorward under easterlies, poleward under westerlies.</summary>
    public float MeridionalShare = 0.35f;

    /// <summary>Systems drift with this share of their row's zonal wind.</summary>
    public float SteeringShare = 0.8f;

    /// <summary>Latitude (rad) over which the circulation round systems fades to nothing at the equator.</summary>
    public float EquatorFade = 5f * MathF.PI / 180f;

    /// <summary>0..1: share of systems that are storms rather than highs, and how hard storms blow.</summary>
    public float Storminess = 0.5f;

    /// <summary>0..1: each band's storminess is scaled by 1 ± this, from the seed.</summary>
    public float StorminessVariation = 0.5f;

    /// <summary>Fair-weather cloud cover between systems, 0..1.</summary>
    public float BaseCloud = 0.15f;

    /// <summary>0..1: each band's base cloud is scaled by 1 ± this, from the seed.</summary>
    public float BaseCloudVariation = 0.5f;

    /// <summary>Multiplies system radii and the spawn cell size (island: 1).</summary>
    public float SizeScale = 1f;

    /// <summary>Multiplies systems' own peak winds (island: 1).</summary>
    public float WindScale = 1f;

    /// <summary>Multiplies systems' lives (island: 1).</summary>
    public float TimeScale = 1f;

    /// <summary>
    /// A rocky world. The band count follows a Rhines-like estimate, 3 × √(ΩR / Earth's ΩR), clamped to 1..6; for the
    /// 1000 km, 6 h worlds that is 2. Override <see cref="ZonalCells"/> to taste.
    /// </summary>
    public static PlanetClimate Rocky(ulong seed, double radius, double daySeconds, double3 spinAxis)
    {
        var spin = 2.0 * Math.PI / daySeconds * radius;
        return new PlanetClimate
        {
            Seed = seed,
            Radius = radius,
            SpinAxis = spinAxis,
            ZonalCells = Math.Clamp((int)Math.Round(3.0 * Math.Sqrt(spin / EarthRotationSpeed)), 1, 6),
        };
    }

    /// <summary>
    /// A gas giant: 4..10 bands per hemisphere from the seed, jets of 50–180 m/s, storms of 1,500–3,000 km with
    /// 130–250 m/s winds that live for days, and an overcast sky.
    /// </summary>
    public static PlanetClimate GasGiant(ulong seed, double radius, double3 spinAxis) => new()
    {
        Seed = seed,
        Radius = radius,
        SpinAxis = spinAxis,
        ZonalCells = 4 + (int)(SphereWeatherField.Mix(seed ^ 0x6A5D39EAE116586DUL) % 7),
        EasterlySpeed = 115f,
        WesterlySpeed = 115f,
        JetVariation = 0.56f,
        MeridionalShare = 0f,
        Storminess = 0.6f,
        StorminessVariation = 0.3f,
        BaseCloud = 0.9f,
        BaseCloudVariation = 0.1f,
        SizeScale = 190f,
        WindScale = 9.6f,
        TimeScale = 200f,
    };
}
