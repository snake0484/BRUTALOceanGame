namespace BrutalGame.DesignKit;

/// <summary>The inspector's search: a name matches when the filter's letters appear in it in order, any case ("wdt" finds Width).</summary>
public static class PropertyFilter
{
    public static bool Matches(ReadOnlySpan<byte> filter, ReadOnlySpan<byte> name)
    {
        var at = 0;

        foreach (var c in filter)
        {
            if (c == (byte)' ')
                continue;

            var lower = Lower(c);

            while (at < name.Length && Lower(name[at]) != lower)
                at++;

            if (at == name.Length)
                return false;

            at++;
        }

        return true;
    }

    private static byte Lower(byte c) => c is >= (byte)'A' and <= (byte)'Z' ? (byte)(c + 32) : c;
}
