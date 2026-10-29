namespace BrutalGame.Terrain.Water;

/// <summary>
/// The colour of water, packed RGBA8 in a uint (r | g &lt;&lt; 8 | b &lt;&lt; 16 | a &lt;&lt; 24), one per cell
/// (<see cref="WaterBlock.Colour"/>; docs/water-fog.md). RGB is the water's own scatter tint (what the fog glows with
/// once lit); A is its fog density: 255 is opaque within one voxel, 0 is unset (a cell never given water). The pipe
/// model carries it with the water, mass-weighted, so a stirred-up pour keeps its silt and sea water stays sea water.
/// </summary>
public static class WaterColour
{
    public const uint None = 0;

    /// <summary>Open sea: clear turquoise, 1/e over 2 m along the eye's ray (alpha 32 of the volume's 4/m full extinction).</summary>
    public const uint Sea = 0x20_38_3D_0A;

    /// <summary>A crater lake: greener, a little murkier.</summary>
    public const uint Lake = 0x30_28_40_14;

    /// <summary>Water poured by a scene (a pond, a flooded room): brownish and turbid.</summary>
    public const uint Pond = 0x60_26_3E_3A;

    public static uint Pack(byte r, byte g, byte b, byte a) => r | (uint)g << 8 | (uint)b << 16 | (uint)a << 24;

    public static byte R(uint c) => (byte)c;

    public static byte G(uint c) => (byte)(c >> 8);

    public static byte B(uint c) => (byte)(c >> 16);

    public static byte A(uint c) => (byte)(c >> 24);

    /// <summary>
    /// The colour of <paramref name="massA"/> of water coloured <paramref name="a"/> mixed with <paramref name="massB"/>
    /// coloured <paramref name="b"/>, channel by channel. Unset water takes the other's colour, so a dry cell that fills
    /// takes what fills it.
    /// </summary>
    public static uint Blend(uint a, float massA, uint b, float massB)
    {
        if (a == None || massA <= 0f)
            return b;

        if (b == None || massB <= 0f)
            return a;

        var total = massA + massB;
        var wa = massA / total;
        var wb = massB / total;
        var r = (byte)(R(a) * wa + R(b) * wb + 0.5f);
        var g = (byte)(G(a) * wa + G(b) * wb + 0.5f);
        var bl = (byte)(B(a) * wa + B(b) * wb + 0.5f);
        var al = (byte)(A(a) * wa + A(b) * wb + 0.5f);
        return Pack(r, g, bl, al);
    }
}
