namespace BrutalGame.Water.Ocean;

/// <summary>Water depth below sea level (m, negative on land) from the world's analytic source; thread-safe, allocation-free.</summary>
public interface IShoreDepth
{
    float Depth(float x, float z);
}
