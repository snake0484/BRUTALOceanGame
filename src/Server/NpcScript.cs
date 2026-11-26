using Brutal.Numerics;
using BrutalGame.Frames;
using BrutalGame.Sim;
using BrutalGame.Sim.Entities;

namespace BrutalGame.Server;

/// <summary>
/// Server-owned test probes spread over every interest class: near the spawn (bubble, full rate), 5-15 km out (10 Hz),
/// 30-60 km out (1 Hz), and on the moon (other SOI, 0.2 Hz). One hopper moves between the planet's rotating and inertial
/// frames every <see cref="HopTicks"/> ticks to exercise frame-change events. Inputs are a function of the tick.
/// </summary>
internal sealed class NpcScript
{
    public const int HopTicks = 600;

    private readonly SimWorld _sim;
    private readonly EntityId[] _npcs;
    private readonly FrameId _surface;
    private readonly FrameId _inertial;

    public NpcScript(SimWorld sim, in Placement spawn, int count)
    {
        _sim = sim;
        _npcs = new EntityId[count];
        var planet = sim.Celestial[1];
        var moon = sim.Celestial[2];
        _surface = planet.Surface;
        _inertial = planet.Inertial;

        for (var i = 0; i < count; i++)
        {
            var placement = spawn;
            var band = i % 4;
            var angle = i * 2.399963;
            var range = band switch
            {
                0 => 100.0 + 40.0 * i,
                1 => 5000.0 + 250.0 * i,
                2 => 30000.0 + 700.0 * i,
                _ => 0.0,
            };

            if (band == 3)
                placement = Placement.At(moon.Surface, new double3(0.0, moon.Radius + 200.0 + i, 0.0));

            placement.Position += new double3(Math.Cos(angle) * range, 0.0, Math.Sin(angle) * range);
            _npcs[i] = sim.Entities.Spawn(EntityKind.Probe, placement);
        }
    }

    public int Count => _npcs.Length;

    public int FrameHops { get; private set; }

    /// <summary>Before <see cref="Net.NetServer.Step"/>: sets each NPC's input for the coming tick.</summary>
    public void Drive()
    {
        var tick = _sim.Clock.TickIndex;
        var entities = _sim.Entities;

        for (var i = 0; i < _npcs.Length; i++)
        {
            if (!entities.IsAlive(_npcs[i]))
                continue;

            var phase = tick * (1.0 / SimWorld.TickRate) * (0.2 + 0.03 * i);
            entities.Input[_npcs[i].Index] = new EntityInput
            {
                MoveZ = (sbyte)(60 + 40 * Math.Sin(phase)),
                Yaw = (sbyte)(50 * Math.Sin(phase * 0.7 + i)),
                Pitch = (sbyte)(10 * Math.Cos(phase * 0.3)),
            };
        }

        if (_npcs.Length == 0 || tick % HopTicks != HopTicks - 1)
            return;

        ref var hopper = ref entities.Placement[_npcs[0].Index];
        var to = hopper.Frame == _surface ? _inertial : _surface;

        if (PlacementFrames.TryMoveTo(_sim.Frames, ref hopper, to))
            FrameHops++;
    }
}
