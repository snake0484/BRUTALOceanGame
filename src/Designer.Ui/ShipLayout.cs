namespace BrutalGame.Designer.Ui;

/// <summary>
/// The ship designer's default docked layout: tools, and the object tree with profiles, on the left; properties, mesh,
/// hull layers, brushes, paint, parts and test bench as tabs top right; stats bottom right; the middle is the 3D view.
/// </summary>
public static class ShipLayout
{
    public const string File = "designer-layout.ini";

    /// <summary>The top-right dock node the edit panels share.</summary>
    public const uint EditDock = 0x00000005;

    /// <summary>The lower-left dock node: the object tree and Profiles.</summary>
    public const uint InteriorDock = 0x00000008;

    public static string Seed(int w, int h)
    {
        var left = Math.Min(440, w / 4);
        var right = Math.Min(420, w / 4);
        var dock = DesignerStyle.DockSpaceId;
        return $"""
            [Window][Designer]
            DockId=0x00000007,0

            [Window][Objects]
            DockId=0x00000008,0

            [Window][Profiles]
            DockId=0x00000008,1

            [Window][Properties]
            DockId=0x00000005,0

            [Window][Mesh]
            DockId=0x00000005,1

            [Window][Hull layers]
            DockId=0x00000005,2

            [Window][Brushes]
            DockId=0x00000005,3

            [Window][Paint]
            DockId=0x00000005,4

            [Window][Parts]
            DockId=0x00000005,5

            [Window][Test bench]
            DockId=0x00000005,6

            [Window][Aero]
            DockId=0x00000005,7

            [Window][Stats]
            DockId=0x00000006,0

            [Docking][Data]
            DockSpace     ID=0x{dock:X8} Pos=0,0 Size={w},{h} Split=X
              DockNode    ID=0x00000001 Parent=0x{dock:X8} SizeRef={left},{h} Split=Y
                DockNode  ID=0x00000007 Parent=0x00000001 SizeRef={left},{h * 62 / 100}
                DockNode  ID=0x00000008 Parent=0x00000001 SizeRef={left},{h * 38 / 100}
              DockNode    ID=0x00000002 Parent=0x{dock:X8} SizeRef={w - left},{h} Split=X
                DockNode  ID=0x00000003 Parent=0x00000002 SizeRef={w - left - right},{h} CentralNode=1
                DockNode  ID=0x00000004 Parent=0x00000002 SizeRef={right},{h} Split=Y
                  DockNode ID=0x00000005 Parent=0x00000004 SizeRef={right},{h * 66 / 100}
                  DockNode ID=0x00000006 Parent=0x00000004 SizeRef={right},{h * 34 / 100}

            """;
    }
}
