using System.Numerics;
using BrutalGame.Terrain.Runtime.Water;
using BrutalGame.Water.Ocean;

namespace BrutalGame;

/// <summary>
/// Hands breaking waves to the zone sim (docs/breaking-waves.md, rendering step 5). The breaker owns the face and lip;
/// once a crest is a bore, the water it throws shoreward belongs to the shallow-water sim: per tick, bore vertices
/// inside a sea zone inject a mass flux (a share of the wave height × the bore speed, per metre of crest) moving
/// shoreward (moving zone water, mass-conserving), and plunging and bore vertices add foam. Outside zones the bore stays visual. Main thread, per tick.
/// </summary>
public sealed class BreakerBores
{
    private const float Gravity = 9.81f;
    private const float MinDepth = 0.3f;

    /// <summary>Share of H × celerity carried as bore mass flux (the heightfield wave already holds the rest).</summary>
    public float BoreFraction = 0.1f;

    /// <summary>Cap on the bore speed √(g (h + H)), m/s.</summary>
    public float MaxSpeed = 6f;

    /// <summary>Foam added per second under a plunging lip and in a bore.</summary>
    public float PlungeFoam = 0.5f;
    public float BoreFoam = 0.15f;

    /// <summary>Bore vertices that landed in a zone last tick, and the volume they handed over per second.</summary>
    public int Injections { get; private set; }

    public float VolumePerSecond { get; private set; }

    public void Apply(BreakerSet breakers, OceanSwell swell, ZoneFlowSim zones, float dt)
    {
        Injections = 0;
        VolumePerSecond = 0f;

        var field = swell.Coastal;
        if (zones.Cells == 0 || field is null || dt <= 0f)
            return;

        var volume = 0f;

        for (var i = 0; i < breakers.Count; i++)
        {
            var b = breakers[i];

            for (var v = 0; v < b.Count; v++)
            {
                ref readonly var vertex = ref b.Vertices[v];
                var bore = vertex.Stage == BreakerStage.Bore;
                if (!bore && vertex.Stage != BreakerStage.Plunging || vertex.Depth < MinDepth)
                    continue;

                var previous = b.Vertices[Math.Max(v - 1, 0)].Position;
                var next = b.Vertices[Math.Min(v + 1, b.Count - 1)].Position;
                var along = next - previous;
                var spacing = 0.5f * along.Length();
                if (spacing < 1e-3f)
                    continue;

                var normal = Vector2.Normalize(new Vector2(-along.Y, along.X));
                if (Vector2.Dot(normal, vertex.Travel) < 0f)
                    normal = -normal;

                var speed = MathF.Min(MathF.Sqrt(Gravity * (vertex.Depth + vertex.Height)), MaxSpeed);
                var mass = bore ? BoreFraction * vertex.Height * speed * spacing * dt : 0f;
                var foam = (bore ? BoreFoam : PlungeFoam) * dt;

                if (!zones.Inject(vertex.Position, mass, normal * speed, vertex.Height, foam, Math.Clamp(0.5f * spacing, 1f, 3f)))
                    continue;

                Injections++;
                volume += mass;
            }
        }

        VolumePerSecond = volume / dt;
    }
}
