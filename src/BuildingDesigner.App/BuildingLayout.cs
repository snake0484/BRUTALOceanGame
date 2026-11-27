namespace BrutalGame.BuildingDesigner.App;

/// <summary>
/// The building designer's default docked layout: the Designer tools over the Building panel on the left; properties,
/// mesh, hull layers and brushes as tabs top right; stats bottom right; the middle is the 3D view.
/// </summary>
public static class BuildingLayout
{
    public const string File = "building-designer-layout.ini";

    public static string Seed(int w, int h)
    {
        var left = Math.Min(440, w / 4);
        var right = Math.Min(420, w / 4);
        var dock = DesignerStyle.DockSpaceId;
        return $"""
            [Window][Designer]
            DockId=0x00000007,0

            [Window][Building]
            DockId=0x00000008,0

            [Window][Properties]
            DockId=0x00000005,0

            [Window][Mesh]
            DockId=0x00000005,1

            [Window][Hull layers]
            DockId=0x00000005,2

            [Window][Brushes]
            DockId=0x00000005,3

            [Window][Stats]
            DockId=0x00000006,0

            [Docking][Data]
            DockSpace     ID=0x{dock:X8} Pos=0,0 Size={w},{h} Split=X
              DockNode    ID=0x00000001 Parent=0x{dock:X8} SizeRef={left},{h} Split=Y
                DockNode  ID=0x00000007 Parent=0x00000001 SizeRef={left},{h * 55 / 100}
                DockNode  ID=0x00000008 Parent=0x00000001 SizeRef={left},{h * 45 / 100}
              DockNode    ID=0x00000002 Parent=0x{dock:X8} SizeRef={w - left},{h} Split=X
                DockNode  ID=0x00000003 Parent=0x00000002 SizeRef={w - left - right},{h} CentralNode=1
                DockNode  ID=0x00000004 Parent=0x00000002 SizeRef={right},{h} Split=Y
                  DockNode ID=0x00000005 Parent=0x00000004 SizeRef={right},{h * 66 / 100}
                  DockNode ID=0x00000006 Parent=0x00000004 SizeRef={right},{h * 34 / 100}

            """;
    }
}
