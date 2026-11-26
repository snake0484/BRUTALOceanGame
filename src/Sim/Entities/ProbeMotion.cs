using Brutal.Numerics;

namespace BrutalGame.Sim.Entities;

/// <summary>
/// Kinematic 6-DOF test body with flight assist. Server and client step owned probes with this same function, so
/// prediction replays exactly what the server ran (one integrator, review C12). Characters (M3) and ships (M6) replace it.
/// </summary>
public static class ProbeMotion
{
    /// <summary>Full-stick acceleration, m/s².</summary>
    public const double Thrust = 12.0;

    /// <summary>Full-stick turn rate, rad/s.</summary>
    public const double TurnRate = 1.5;

    /// <summary>Flight assist velocity damping, 1/s.</summary>
    public const double Assist = 0.8;

    private const double AxisScale = 1.0 / 127.0;

    public static void Step(ref Placement placement, in EntityInput input, double dt)
    {
        var stick = new double3(input.MoveX, input.MoveY, input.MoveZ) * (AxisScale * Thrust);
        var velocity = placement.Velocity + (placement.Rotation * stick) * dt;
        velocity -= velocity * (Assist * dt);
        placement.Velocity = velocity;
        placement.Position += velocity * dt;

        var rates = new double3(input.Pitch, input.Yaw, input.Roll) * (AxisScale * TurnRate);
        var rate = rates.Length();
        placement.AngularVelocity = VectorCast.ToFloat(placement.Rotation * rates);

        if (rate == 0.0)
            return;

        var turn = doubleQuat.CreateFromAxisAngle(rates / rate, rate * dt);
        placement.Rotation = doubleQuat.Normalize(placement.Rotation * turn);
    }
}
