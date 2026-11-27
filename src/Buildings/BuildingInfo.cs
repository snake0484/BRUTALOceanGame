using BrutalGame.Ships;

namespace BrutalGame.Buildings;

/// <summary>
/// What a building adds to its hull plan (docs/pivot/briefs/building-designer-brief.md). Building-local frame: +Y up,
/// y = 0 is finished ground, the pad centred on the origin. Pad half extents in 1/256 m (as solid half extents), foundation
/// depth in 1/1024 m (as solid positions).
/// </summary>
public readonly record struct BuildingInfo(uint PadHalfX, uint PadHalfZ, int FoundationDepth)
{
    public const int SizeInBytes = 12;
    public const double MaxPadMetres = 500.0;
    public const double MaxFoundationMetres = 64.0;

    public static readonly BuildingInfo Default = FromMetres(12.0, 12.0, 1.0);

    public double PadWidthMetres => PadHalfX * 2.0 * HullSolid.MetresPerHalfUnit;

    public double PadDepthMetres => PadHalfZ * 2.0 * HullSolid.MetresPerHalfUnit;

    public double FoundationMetres => FoundationDepth * HullSolid.MetresPerPositionUnit;

    public bool IsValid =>
        PadHalfX > 0 && PadHalfZ > 0 &&
        PadHalfX <= MaxPadMetres * HullSolid.HalfUnitsPerMetre && PadHalfZ <= MaxPadMetres * HullSolid.HalfUnitsPerMetre &&
        FoundationDepth >= 0 && FoundationDepth <= MaxFoundationMetres * HullSolid.PositionUnitsPerMetre;

    public static BuildingInfo FromMetres(double padWidth, double padDepth, double foundation) => new(
        (uint)Math.Round(Math.Clamp(padWidth, 0.25, MaxPadMetres * 2.0) * 0.5 * HullSolid.HalfUnitsPerMetre),
        (uint)Math.Round(Math.Clamp(padDepth, 0.25, MaxPadMetres * 2.0) * 0.5 * HullSolid.HalfUnitsPerMetre),
        (int)Math.Round(Math.Clamp(foundation, 0.0, MaxFoundationMetres) * HullSolid.PositionUnitsPerMetre));
}
