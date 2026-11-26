namespace BrutalGame.Net.Transport;

/// <summary>
/// In-process network for tests and listen servers: fixed latency in steps of <see cref="Now"/>, seeded loss on
/// unreliable messages, FIFO delivery (so sequenced messages never reorder). Preallocated rings; no allocation after
/// construction.
/// </summary>
public sealed class LoopbackNetwork(int latencySteps = 0, double unreliableLoss = 0.0, ulong seed = 1)
{
    /// <summary>Delivery clock; advance it once per server tick.</summary>
    public long Now;

    public int LatencySteps = latencySteps;
    public double UnreliableLoss = unreliableLoss;

    /// <summary>Milliseconds per step, for <see cref="ConnectionStats.RoundTripMs"/>.</summary>
    public double StepMs = 1000.0 / 60.0;

    private ulong _random = seed | 1;

    public LoopbackTransport Listen(int maxConnections) => new(this, maxConnections, null);

    public LoopbackTransport Connect(LoopbackTransport server)
    {
        var client = new LoopbackTransport(this, 1, server);
        var slot = server.Accept(client);

        if (slot < 0)
        {
            client.Inbox.Push(Now + LatencySteps, 0, TransportEventKind.Failed, default);
            return client;
        }

        client.PeerSlot = slot;
        client.Peers[0] = server;
        client.Inbox.Push(Now + 2L * LatencySteps, 0, TransportEventKind.Connected, default);
        return client;
    }

    internal bool Drop(Delivery delivery)
    {
        if (delivery == Delivery.ReliableOrdered || UnreliableLoss <= 0.0)
            return false;

        _random ^= _random << 13;
        _random ^= _random >> 7;
        _random ^= _random << 17;
        return (_random >> 11) * (1.0 / (1UL << 53)) < UnreliableLoss;
    }
}

public sealed class LoopbackTransport : ITransport
{
    internal readonly LoopbackInbox Inbox = new();
    internal readonly LoopbackTransport?[] Peers;
    internal int PeerSlot;

    private readonly LoopbackNetwork _network;
    private readonly bool _client;
    private readonly long[] _sent;
    private readonly long[] _received;

    internal LoopbackTransport(LoopbackNetwork network, int maxConnections, LoopbackTransport? server)
    {
        _network = network;
        Peers = new LoopbackTransport?[maxConnections];
        _sent = new long[maxConnections];
        _received = new long[maxConnections];
        _client = server is not null;
    }

    public int MaxConnections => Peers.Length;

    /// <summary>Our connection number on the other end: our slot on the server, or 0 (the server) on a client.</summary>
    private int SlotAtPeer => _client ? PeerSlot : 0;

    public bool Poll(out TransportEvent e)
    {
        if (!Inbox.TryPop(_network.Now, out var connection, out var kind, out var data))
        {
            e = default;
            return false;
        }

        if (kind == TransportEventKind.Data)
            _received[connection] += data.Length;

        e = new TransportEvent(kind, connection, data);
        return true;
    }

    public void Send(int connection, ReadOnlySpan<byte> data, Delivery delivery, byte channel)
    {
        if ((uint)connection >= (uint)Peers.Length || Peers[connection] is not { } peer)
            return;

        _sent[connection] += data.Length;

        if (_network.Drop(delivery))
            return;

        peer.Inbox.Push(_network.Now + _network.LatencySteps, SlotAtPeer, TransportEventKind.Data, data);
    }

    public void Disconnect(int connection)
    {
        if ((uint)connection >= (uint)Peers.Length || Peers[connection] is not { } peer)
            return;

        Peers[connection] = null;
        var slot = SlotAtPeer;
        peer.Inbox.Push(_network.Now + _network.LatencySteps, slot, TransportEventKind.Disconnected, default);
        peer.Peers[slot] = null;
    }

    public ConnectionStats Stats(int connection) => new()
    {
        PayloadSent = _sent[connection],
        PayloadReceived = _received[connection],
        WireSent = _sent[connection],
        WireReceived = _received[connection],
        RoundTripMs = (int)(2 * _network.LatencySteps * _network.StepMs),
    };

    public void Dispose()
    {
        for (var i = 0; i < Peers.Length; i++)
            Disconnect(i);
    }

    internal int Accept(LoopbackTransport client)
    {
        for (var i = 0; i < Peers.Length; i++)
        {
            if (Peers[i] is not null)
                continue;

            Peers[i] = client;
            _sent[i] = 0;
            _received[i] = 0;
            Inbox.Push(_network.Now + _network.LatencySteps, i, TransportEventKind.Connected, default);
            return i;
        }

        return -1;
    }
}

/// <summary>FIFO of timed messages over a byte arena ring; popped data stays valid until the next pop.</summary>
internal sealed class LoopbackInbox
{
    private const int MaxMessages = 16384;
    private const int ArenaBytes = 8 << 20;

    private readonly long[] _due = new long[MaxMessages];
    private readonly int[] _connection = new int[MaxMessages];
    private readonly TransportEventKind[] _kind = new TransportEventKind[MaxMessages];
    private readonly int[] _offset = new int[MaxMessages];
    private readonly int[] _length = new int[MaxMessages];
    private readonly byte[] _arena = new byte[ArenaBytes];
    private int _head, _count, _write;

    public void Push(long due, int connection, TransportEventKind kind, ReadOnlySpan<byte> data)
    {
        if (_count == MaxMessages)
            throw new InvalidOperationException("Loopback inbox full");

        if (_write + data.Length > ArenaBytes)
            _write = 0;

        var slot = (_head + _count) % MaxMessages;
        _due[slot] = due;
        _connection[slot] = connection;
        _kind[slot] = kind;
        _offset[slot] = _write;
        _length[slot] = data.Length;
        data.CopyTo(_arena.AsSpan(_write));
        _write += data.Length;
        _count++;
    }

    public bool TryPop(long now, out int connection, out TransportEventKind kind, out ReadOnlySpan<byte> data)
    {
        if (_count == 0 || _due[_head] > now)
        {
            connection = 0;
            kind = default;
            data = default;
            return false;
        }

        connection = _connection[_head];
        kind = _kind[_head];
        data = _arena.AsSpan(_offset[_head], _length[_head]);
        _head = (_head + 1) % MaxMessages;
        _count--;
        return true;
    }
}
