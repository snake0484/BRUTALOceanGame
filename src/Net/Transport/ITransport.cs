namespace BrutalGame.Net.Transport;

public enum Delivery : byte
{
    Unreliable,

    /// <summary>Unreliable, and older packets arriving after newer ones are dropped.</summary>
    UnreliableSequenced,

    ReliableOrdered,
}

public enum TransportEventKind : byte
{
    Connected,
    Disconnected,

    /// <summary>A connect attempt failed or was refused (client side).</summary>
    Failed,

    Data,
}

/// <summary>One received event. <see cref="Data"/> is valid until the next <see cref="ITransport.Poll"/>.</summary>
public readonly ref struct TransportEvent(TransportEventKind kind, int connection, ReadOnlySpan<byte> data)
{
    public readonly TransportEventKind Kind = kind;
    public readonly int Connection = connection;
    public readonly ReadOnlySpan<byte> Data = data;
}

/// <summary>Byte counts for one connection: payload is what we sent or received, wire adds the transport's own headers, acks and resends.</summary>
public struct ConnectionStats
{
    public long PayloadSent, PayloadReceived;
    public long WireSent, WireReceived;
    public int RoundTripMs;
    public float PacketLoss;
}

/// <summary>
/// Message transport. Connections are small slot numbers; a client's server is slot 0. Main-thread only; Poll and Send
/// never allocate.
/// </summary>
public interface ITransport : IDisposable
{
    int MaxConnections { get; }

    bool Poll(out TransportEvent e);

    void Send(int connection, ReadOnlySpan<byte> data, Delivery delivery, byte channel);

    void Disconnect(int connection);

    ConnectionStats Stats(int connection);
}

/// <summary>Ordering channels: reliable events and snapshots never wait on each other.</summary>
public static class Channels
{
    public const byte Events = 0;
    public const byte Snapshots = 1;
    public const byte Inputs = 2;
}
