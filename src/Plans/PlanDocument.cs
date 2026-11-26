using BrutalGame.Ships;

namespace BrutalGame.Plans;

/// <summary>Growable brush array in evaluation order. Span access, no per-call allocation.</summary>
public sealed class BrushList
{
    private HullBrush[] _items = new HullBrush[16];

    public int Count { get; private set; }

    public Span<HullBrush> AsSpan() => _items.AsSpan(0, Count);

    public ref HullBrush this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            return ref _items[index];
        }
    }

    public void Add(in HullBrush brush) => Insert(Count, brush);

    public void Insert(int index, in HullBrush brush)
    {
        if ((uint)index > (uint)Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        if (Count == _items.Length)
            Array.Resize(ref _items, _items.Length * 2);

        Array.Copy(_items, index, _items, index + 1, Count - index);
        _items[index] = brush;
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
/// brush list) plus <see cref="PlanMeta"/>. Brush ids are allocated here, monotonic, never reused.
/// </summary>
public sealed class PlanDocument
{
    public PlanLevel Level = PlanLevel.Ship;
    public PlanClass Class = PlanClass.Ship;
    public uint DataVersion;
    public PlanHash Parent;
    public PlanMeta Meta = new();
    public readonly BrushList Brushes = new();

    /// <summary>Material table: <see cref="HullBrush.Material"/> indexes it.</summary>
    public readonly List<DefId> Materials = new();

    /// <summary>Mesh table (D11): a <see cref="BrushShape.Mesh"/> brush's Param indexes it; brushes may share entries.</summary>
    public readonly List<HullMesh> Meshes = new();

    public uint AllocateId() => Meta.NextId++;

    /// <summary>Index of <paramref name="material"/> in the table, or -1.</summary>
    public int MaterialIndex(DefId material) => Materials.IndexOf(material);

    /// <summary>Moves <see cref="PlanMeta.NextId"/> past every brush id (after a load without META).</summary>
    public void FixNextId()
    {
        foreach (ref readonly var brush in Brushes.AsSpan())
        {
            if (brush.Id >= Meta.NextId)
                Meta.NextId = brush.Id + 1;
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

        foreach (ref readonly var brush in Brushes.AsSpan())
            copy.Brushes.Add(brush);

        return copy;
    }
}
