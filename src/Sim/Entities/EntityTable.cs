namespace BrutalGame.Sim.Entities;

/// <summary>
/// Every entity, struct-of-arrays indexed by <see cref="EntityId.Index"/> (docs/pivot/review.md, Missing 2). Preallocated:
/// spawn and despawn never allocate, and live slots are kept dense in <see cref="Alive"/> for iteration. Main-thread only.
/// </summary>
public sealed class EntityTable
{
    public const int DefaultCapacity = 4096;
    public const int NoOwner = -1;

    public readonly EntityKind[] Kind;
    public readonly Placement[] Placement;

    /// <summary>Owning player slot, <see cref="NoOwner"/> for the server.</summary>
    public readonly int[] Owner;

    /// <summary>Id on the wire; 0 is never used. Servers hand them out, clients take the server's.</summary>
    public readonly uint[] NetId;

    /// <summary>Control input the next tick applies.</summary>
    public readonly EntityInput[] Input;

    private readonly int[] _generation;
    private readonly int[] _free;
    private readonly int[] _alive;
    private readonly int[] _aliveSlot;
    private int _freeCount;
    private uint _nextNetId = 1;

    public EntityTable(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        Kind = new EntityKind[capacity];
        Placement = new Placement[capacity];
        Owner = new int[capacity];
        NetId = new uint[capacity];
        Input = new EntityInput[capacity];
        _generation = new int[capacity];
        _free = new int[capacity];
        _alive = new int[capacity];
        _aliveSlot = new int[capacity];

        for (var i = 0; i < capacity; i++)
            _free[i] = capacity - 1 - i;

        _freeCount = capacity;
    }

    public int Capacity => Kind.Length;

    public int Count { get; private set; }

    /// <summary>Indices of live entities, in no fixed order (despawn swaps the last one in).</summary>
    public ReadOnlySpan<int> Alive => _alive.AsSpan(0, Count);

    /// <summary>Spawns an entity. A zero <paramref name="netId"/> takes the next server id. Throws when full.</summary>
    public EntityId Spawn(EntityKind kind, in Placement placement, int owner = NoOwner, uint netId = 0)
    {
        if (_freeCount == 0)
            throw new InvalidOperationException($"Entity table full ({Capacity})");

        var index = _free[--_freeCount];
        Kind[index] = kind;
        Placement[index] = placement;
        Owner[index] = owner;
        NetId[index] = netId != 0 ? netId : _nextNetId++;
        Input[index] = default;

        _aliveSlot[index] = Count;
        _alive[Count++] = index;
        return new EntityId(index, _generation[index]);
    }

    public bool Despawn(EntityId id)
    {
        if (!IsAlive(id))
            return false;

        var index = id.Index;
        var slot = _aliveSlot[index];
        var last = _alive[--Count];
        _alive[slot] = last;
        _aliveSlot[last] = slot;

        Kind[index] = EntityKind.None;
        NetId[index] = 0;
        _generation[index]++;
        _free[_freeCount++] = index;
        return true;
    }

    public bool IsAlive(EntityId id)
    {
        return (uint)id.Index < (uint)Capacity && Kind[id.Index] != EntityKind.None && _generation[id.Index] == id.Generation;
    }

    /// <summary>Id of the live entity in <paramref name="index"/>.</summary>
    public EntityId IdOf(int index) => new(index, _generation[index]);
}
