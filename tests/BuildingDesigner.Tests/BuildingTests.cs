using BrutalGame.Buildings;
using BrutalGame.Designer;
using BrutalGame.Plans;
using BrutalGame.Ships;

namespace BrutalGame.BuildingDesigner.Tests;

public class BuildingTests
{
    private static readonly DefId Wall = DefId.FromName("mat.hull.steel");
    private static readonly DefId Roof = DefId.FromName("mat.hull.titanium");

    private static BuildingSession Hut()
    {
        var session = new BuildingSession();
        SampleBuildings.Hut(session.Plan, Wall, Roof);
        return session;
    }

    [Fact]
    public void HutIsAddedAndCutSolids()
    {
        var plan = Hut().Plan.Doc;
        var adds = 0;
        var cuts = 0;

        foreach (ref readonly var s in plan.Solids.AsSpan())
        {
            if (s.Op == SolidOp.Add)
                adds++;
            else if (s.Op == SolidOp.Subtract)
                cuts++;
        }

        Assert.Equal(3, adds);
        Assert.Equal(3, cuts);
        Assert.Equal(PlanClass.Station, plan.Class);
    }

    [Fact]
    public void FileRoundTripIsByteStable()
    {
        var session = Hut();
        session.SetInfo(BuildingInfo.FromMetres(14, 10, 1.5));
        var bytes = BuildingFile.Encode(session.Info, session.Plan.Doc, out var hash);

        Assert.Equal(BuildingFileError.None, BuildingFile.TryDecode(bytes, out var info, out var plan, out var read));
        Assert.Equal(hash, read);
        Assert.Equal(session.Info, info);
        Assert.Equal(session.Plan.Doc.Solids.Count, plan.Solids.Count);

        var again = BuildingFile.Encode(info, plan, out var hashAgain);
        Assert.Equal(hash, hashAgain);
        Assert.Equal(bytes, again);
    }

    [Fact]
    public void HashFollowsInfoAndBodyNotName()
    {
        var session = Hut();
        BuildingFile.Encode(session.Info, session.Plan.Doc, out var hash);
        session.Plan.Doc.Meta.Name = "Renamed hut";
        BuildingFile.Encode(session.Info, session.Plan.Doc, out var renamed);
        BuildingFile.Encode(BuildingInfo.FromMetres(20, 20, 1), session.Plan.Doc, out var bigger);

        Assert.Equal(hash, renamed);
        Assert.NotEqual(hash, bigger);
    }

    [Fact]
    public void CorruptFilesAreRefused()
    {
        var session = Hut();
        var bytes = BuildingFile.Encode(session.Info, session.Plan.Doc, out _);

        var info = (byte[])bytes.Clone();
        info[BuildingFile.HeaderBytes] ^= 1;
        Assert.Equal(BuildingFileError.HashMismatch, BuildingFile.TryDecode(info, out _, out _, out _));

        var magic = (byte[])bytes.Clone();
        magic[0] ^= 1;
        Assert.Equal(BuildingFileError.BadMagic, BuildingFile.TryDecode(magic, out _, out _, out _));

        Assert.Equal(BuildingFileError.BadLengths, BuildingFile.TryDecode(bytes.AsSpan(0, bytes.Length - 1), out _, out _, out _));

        var plan = (byte[])bytes.Clone();
        plan[^1] ^= 0x55;
        Assert.NotEqual(BuildingFileError.None, BuildingFile.TryDecode(plan, out _, out _, out _));
    }

    [Fact]
    public void InfoAndPlanEditsShareOneHistory()
    {
        var session = new BuildingSession();
        var start = session.Info;
        var wide = BuildingInfo.FromMetres(30, 12, 1);
        HullCommands.AddSolid(session.Plan, SolidShape.Box, SolidOp.Add, 0, 2048, 0, 512, 512, 512, SolidRotation.Identity, Wall);
        Assert.True(session.SetInfo(wide));
        HullCommands.AddSolid(session.Plan, SolidShape.Box, SolidOp.Add, 8192, 2048, 0, 512, 512, 512, SolidRotation.Identity, Wall);
        session.Sync();

        Assert.True(session.Undo());
        Assert.Equal(1, session.Plan.Doc.Solids.Count);
        Assert.Equal(wide, session.Info);

        Assert.True(session.Undo());
        Assert.Equal(start, session.Info);
        Assert.Equal(1, session.Plan.Doc.Solids.Count);

        Assert.True(session.Redo());
        Assert.Equal(wide, session.Info);
        Assert.True(session.Redo());
        Assert.Equal(2, session.Plan.Doc.Solids.Count);

        // The Designer panel's own Undo goes straight to the plan; Sync brings the info along.
        session.Plan.UndoLast();
        session.Plan.UndoLast();
        session.Sync();
        Assert.Equal(start, session.Info);
    }

    [Fact]
    public void NewEditDropsInfoRedo()
    {
        var session = new BuildingSession();
        var wide = BuildingInfo.FromMetres(30, 12, 1);
        session.SetInfo(wide);
        session.Undo();
        HullCommands.AddSolid(session.Plan, SolidShape.Box, SolidOp.Add, 0, 2048, 0, 512, 512, 512, SolidRotation.Identity, Wall);
        session.Sync();

        Assert.False(session.Redo());
        Assert.Equal(BuildingInfo.Default, session.Info);
    }

    [Fact]
    public void GroundChecks()
    {
        var session = Hut();
        Assert.Equal(0, BuildingCheck.OutsidePad(session.Plan.Doc, session.Info));
        Assert.Equal(0, BuildingCheck.BelowFoundation(session.Plan.Doc, session.Info));

        var narrow = BuildingInfo.FromMetres(6, 12, 0.5);
        Assert.True(BuildingCheck.OutsidePad(session.Plan.Doc, narrow) > 0);
        Assert.True(BuildingCheck.BelowFoundation(session.Plan.Doc, narrow) > 0);
    }

    [Fact]
    public void SettlementRoundTrip()
    {
        var doc = new SettlementDocument { Name = "Outpost A", PadHalfX = 40 * 256, PadHalfZ = 30 * 256 };
        doc.Placements.Add(new BuildingPlacement { Building = "outpost.hab-a", Hash = 0x1234, PosX = -10240, PosY = 0, PosZ = 5120, Yaw = 6 });
        doc.Placements.Add(new BuildingPlacement { Building = "outpost.tank", Hash = 0xBEEF, PosX = 20480, PosY = 512, PosZ = -2048, Yaw = 23 });
        var bytes = SettlementFile.Encode(doc, out var hash);

        Assert.Equal(SettlementFileError.None, SettlementFile.TryDecode(bytes, out var read, out var readHash));
        Assert.Equal(hash, readHash);
        Assert.Equal(doc.Name, read.Name);
        Assert.Equal((doc.PadHalfX, doc.PadHalfZ), (read.PadHalfX, read.PadHalfZ));
        Assert.Equal(doc.Placements, read.Placements);
        Assert.Equal(bytes, SettlementFile.Encode(read, out _));

        bytes[^1] ^= 1;
        Assert.Equal(SettlementFileError.HashMismatch, SettlementFile.TryDecode(bytes, out _, out _));
    }

    [Fact]
    public void SettlementRefusesBadYaw()
    {
        var doc = new SettlementDocument { Name = "x" };
        doc.Placements.Add(new BuildingPlacement { Building = "b", Yaw = BuildingPlacement.YawSteps });
        Assert.Throws<InvalidOperationException>(() => SettlementFile.Encode(doc, out _));
    }
}
