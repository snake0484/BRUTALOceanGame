namespace BrutalGame.Sim.Entities;

/// <summary>Slot in <see cref="EntityTable"/> plus the generation that slot had at spawn, so stale ids never alias a reused slot.</summary>
public readonly record struct EntityId(int Index, int Generation)
{
    public static readonly EntityId None = new(-1, 0);

    public bool IsValid => Index >= 0;
}

public enum EntityKind : byte
{
    None,
    Character,
    Ship,

    /// <summary>Test body moved by <see cref="ProbeMotion"/>: the stand-in for characters and ships until M3 and M6.</summary>
    Probe,
}
