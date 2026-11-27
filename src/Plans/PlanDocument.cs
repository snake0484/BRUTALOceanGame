using BrutalGame.Ships;
using BrutalGame.Ships.Components;
using BrutalGame.Ships.Interior;

namespace BrutalGame.Plans;

/// <summary>Growable solid array in evaluation order. Span access, no per-call allocation.</summary>
public sealed class SolidList
{
    private HullSolid[] _items = new HullSolid[16];

    public int Count { get; private set; }

    public Span<HullSolid> AsSpan() => _items.AsSpan(0, Count);

    public ref HullSolid this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            return ref _items[index];
        }
    }

    public void Add(in HullSolid solid) => Insert(Count, solid);

    public void Insert(int index, in HullSolid solid)
    {
        if ((uint)index > (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        if (Count == _items.Length)
            Array.Resize(ref _items, _items.Length * 2);

        Array.Copy(_items, index, _items, index + 1, Count - index);
        _items[index] = solid;
        Count++;
    }

    public void RemoveAt(int index)
    {
        if ((uint)index >= (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        Count--;
        Array.Copy(_items, index + 1, _items, index, Count - index);
    }

    public void Clear() => Count = 0;

    public int IndexOf(uint id)
    {
        for (var i = 0; i < Count; i++)
        {
            if (_items[i].Id == id)
                return i;
        }

        return -1;
    }
}

/// <summary>Not hashed: renaming or re-tagging never changes a plan's identity. <see cref="NextId"/> is edit state.</summary>
public sealed class PlanMeta
{
    public string Name = "";
    public string Description = "";
    public string Author = "";
    public uint Revision;
    public uint NextId = 1;
    public readonly List<string> Tags = new();
}

/// <summary>
/// A hull plan (ship or hull chunk level) in memory: BODY fields (level, class, data version, parent, material table,
/// solid list) plus <see cref="PlanMeta"/>. Solid ids are allocated here, monotonic, never reused.
/// </summary>
public sealed class PlanDocument
{
    public PlanLevel Level = PlanLevel.Ship;
    public PlanClass Class = PlanClass.Ship;
    public uint DataVersion;
    public PlanHash Parent;
    public PlanMeta Meta = new();
    public readonly SolidList Solids = new();

    /// <summary>Material table: <see cref="HullSolid.Material"/> indexes it.</summary>
    public readonly List<DefId> Materials = new();

    /// <summary>Mesh table (D11): a <see cref="SolidShape.Mesh"/> solid's Param indexes it; solids may share entries.</summary>
    public readonly List<HullMesh> Meshes = new();

    /// <summary>Layered hull (skin, sections, depth stacks, rooms, components, panels); null for a solid hull.</summary>
    public HullLayering? Layers;

    /// <summary>Brushes (stamp meshes, swept profiles) and the strokes drawn with them; null when there are none.</summary>
    public HullBrushSet? Brushes;

    /// <summary>Parts and pipes (a component plan) and placed component prefabs (a ship plan); null when there are none.</summary>
    public ComponentData? Components;

    /// <summary>Interior spaces (hallways); null when there are none.</summary>
    public HullInterior? Interior;

    /// <summary>Windows on the hull; null when there are none.</summary>
    public HullWindowSet? Windows;

    /// <summary>Decals on the hull; null when there are none.</summary>
    public HullDecalSet? Decals;

    /// <summary>Paint slots, the hull's base paint and brush strokes' own paints; null when there are none.</summary>
    public HullPaintSet? Paint;

    /// <summary>Rotors and hinges and the solids that move with them; null when there are none.</summary>
    public HullJointSet? Joints;

    /// <summary>Doorways and docking port faces (a component's); null when there are none.</summary>
    public HullOpeningSet? Openings;

    /// <summary>The component's programme: the interface it implements, its node graph, its states' poses; null when none.</summary>
    public Ships.Logic.HullProgram? Program;

    /// <summary>The component's function cores (sensors and effectors, D88); null when there are none.</summary>
    public Ships.Logic.HullCoreSet? Cores;

    /// <summary>
    /// The component prefabs this plan places, by plan hash, carried inside it so a file or a network send is complete.
    /// Each is a component-level plan, never edited from here (a new design is a new hash).
    /// </summary>
    public readonly SortedDictionary<ulong, PlanDocument> Prefabs = new();

    public uint AllocateId() => Meta.NextId++;

    /// <summary>Index of <paramref name="material"/> in the table, or -1.</summary>
    public int MaterialIndex(DefId material) => Materials.IndexOf(material);

    /// <summary>Moves <see cref="PlanMeta.NextId"/> past every solid id (after a load without META).</summary>
    public void FixNextId()
    {
        foreach (ref readonly var solid in Solids.AsSpan())
        {
            if (solid.Id >= Meta.NextId)
                Meta.NextId = solid.Id + 1;
        }

        if (Brushes is not null)
        {
            foreach (var stroke in Brushes.Strokes)
            {
                if (stroke.Id >= Meta.NextId)
                    Meta.NextId = stroke.Id + 1;
            }
        }

        if (Components is not null)
        {
            foreach (var p in Components.Parts)
                Meta.NextId = Math.Max(Meta.NextId, p.Id + 1);

            foreach (var p in Components.Pipes)
                Meta.NextId = Math.Max(Meta.NextId, p.Id + 1);

            foreach (var p in Components.Placements)
                Meta.NextId = Math.Max(Meta.NextId, p.Id + 1);
        }

        if (Interior is not null)
        {
            foreach (var h in Interior.Hallways)
                Meta.NextId = Math.Max(Meta.NextId, h.Id + 1);
        }

        if (Windows is not null)
        {
            foreach (var w in Windows.Windows)
                Meta.NextId = Math.Max(Meta.NextId, w.Id + 1);
        }

        if (Decals is not null)
        {
            foreach (var d in Decals.Decals)
                Meta.NextId = Math.Max(Meta.NextId, d.Id + 1);
        }

        if (Joints is not null)
        {
            foreach (var j in Joints.Joints)
                Meta.NextId = Math.Max(Meta.NextId, j.Id + 1);
        }

        if (Openings is not null)
        {
            foreach (var o in Openings.Openings)
                Meta.NextId = Math.Max(Meta.NextId, o.Id + 1);
        }
    }

    public PlanDocument Clone()
    {
        var copy = new PlanDocument { Level = Level, Class = Class, DataVersion = DataVersion, Parent = Parent };
        copy.Meta = new PlanMeta
        {
            Name = Meta.Name,
            Description = Meta.Description,
            Author = Meta.Author,
            Revision = Meta.Revision,
            NextId = Meta.NextId,
        };
        copy.Meta.Tags.AddRange(Meta.Tags);
        copy.Materials.AddRange(Materials);
        copy.Meshes.AddRange(Meshes);
        copy.Layers = Layers?.Clone();
        copy.Brushes = Brushes?.Clone();
        copy.Components = Components?.Clone();
        copy.Interior = Interior?.Clone();
        copy.Windows = Windows?.Clone();
        copy.Decals = Decals?.Clone();
        copy.Paint = Paint?.Clone();
        copy.Joints = Joints?.Clone();
        copy.Openings = Openings?.Clone();
        copy.Program = Program?.Clone();
        copy.Cores = Cores?.Clone();

        // Prefabs are never edited in place: sharing them is safe.
        foreach (var (hash, prefab) in Prefabs)
            copy.Prefabs[hash] = prefab;

        foreach (ref readonly var solid in Solids.AsSpan())
            copy.Solids.Add(solid);

        return copy;
    }
}
