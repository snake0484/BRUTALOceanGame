namespace BrutalGame.Menu;

public readonly record struct SaveSlot(string Id, string Name, ulong Seed, DateTime SavedUtc, TimeSpan Played);

/// <summary>The save slots the menu lists under Continue and Load, newest first.</summary>
public interface ISaveCatalog
{
    ReadOnlySpan<SaveSlot> Slots { get; }

    /// <summary>Re-reads the slot list. Called when the menu opens, never per frame.</summary>
    void Refresh();
}

/// <summary>Stub until the save milestone: no slots, so Continue and Load stay greyed out.</summary>
public sealed class EmptySaveCatalog : ISaveCatalog
{
    public ReadOnlySpan<SaveSlot> Slots => default;

    public void Refresh()
    {
    }
}
