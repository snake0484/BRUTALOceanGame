namespace BrutalGame.Physics;

/// <summary>Opaque body reference. <c>Id</c> is the Jolt BodyID + 1 so <c>default</c> is invalid.</summary>
public readonly record struct BodyHandle(uint Id)
{
    public bool IsValid => Id != 0;

    internal uint JoltId => Id - 1;

    internal static BodyHandle FromJolt(uint joltId) => new(joltId + 1);
}
