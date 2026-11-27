using System.Numerics;
using BrutalGame.Water.Ocean;

namespace BrutalGame.Water.Coast;

/// <summary>
/// A coast region placed against the camera for the GPU: the map from the sea frame's plane (x, z relative to the
/// camera) to the region's cell coordinates, through the <see cref="SeaSlots"/> slot of its tile (linearised at the
/// camera, as the swell's is). The window's corner against the camera is the only double step.
/// </summary>
public unsafe struct CoastPlacement
{
    /// <summary>The slot whose tile is the region's.</summary>
    public int Slot;

    /// <summary>Cell x = X x + Y z + Z, and likewise cell z.</summary>
    public Vector4 CellX;
    public Vector4 CellZ;

    /// <summary>False if the region's tile is none of the slots (out of the drawn sea's reach).</summary>
    public static bool Of(in CoastRegion region, in SeaSlots slots, out CoastPlacement placement)
    {
        placement = default;

        for (var s = 0; s < slots.Count; s++)
        {
            if (slots.Tile[s] != region.Key.Tile)
                continue;

            const float perCell = 1f / CoastWindow.CellSize;
            placement.Slot = s;
            placement.CellX = new Vector4(slots.ToTile[4 * s] * perCell, slots.ToTile[4 * s + 1] * perCell, (float)((slots.U[s] - region.Window.OriginU) * perCell), 0f);
            placement.CellZ = new Vector4(slots.ToTile[4 * s + 2] * perCell, slots.ToTile[4 * s + 3] * perCell, (float)((slots.V[s] - region.Window.OriginV) * perCell), 0f);
            return true;
        }

        return false;
    }
}
