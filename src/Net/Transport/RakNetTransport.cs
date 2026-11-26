using System.Text;
using Brutal.RakNetApi;

namespace BrutalGame.Net.Transport;

/// <summary>
/// <see cref="ITransport"/> over a RakNet peer (Brutal.RakNet). Connection slots map RakNet GUIDs; a client has the
/// server in slot 0. The packet returned by Poll is released on the next Poll, so <see cref="TransportEvent.Data"/>
/// borrows RakNet's buffer with no copy.
/// </summary>
public sealed unsafe class RakNetTransport : ITransport
{
    private const ulong NoGuid = ulong.MaxValue;
    private const byte FirstUserId = (byte)DefaultMessageIDTypes.UserPacketEnum;
    private const int StatWireSent = 5;
    private const int StatWireReceived = 6;

    private readonly ulong[] _guids;
    private readonly SystemAddress[] _addresses;
    private readonly long[] _payloadSent;
    private readonly long[] _payloadReceived;
    private RakPeerInstance _peer;
    private Packet* _current;

    private RakNetTransport(int maxConnections)
    {
        _peer = RakNetLibrary.CreateInstance();

        if (_peer.IsNull())
            throw new InvalidOperationException("RakNet CreateInstance failed");

        _guids = new ulong[maxConnections];
        _addresses = new SystemAddress[maxConnections];
        _payloadSent = new long[maxConnections];
        _payloadReceived = new long[maxConnections];
        Array.Fill(_guids, NoGuid);
    }

    public int MaxConnections => _guids.Length;

    /// <summary>A server listening on <paramref name="port"/> for up to <paramref name="maxConnections"/> clients.</summary>
    public static RakNetTransport Listen(ushort port, int maxConnections)
    {
        var transport = new RakNetTransport(maxConnections);
        Span<SocketDescriptor> socket = [new SocketDescriptor(port)];
        transport.Start((uint)maxConnections, socket);
        transport._peer.SetMaximumIncomingConnections((ushort)maxConnections);
        return transport;
    }

    /// <summary>A client connecting to <paramref name="host"/>; Poll reports Connected or Failed.</summary>
    public static RakNetTransport Connect(string host, ushort port)
    {
        var transport = new RakNetTransport(1);
        Span<SocketDescriptor> socket = [new SocketDescriptor()];
        transport.Start(1, socket);

        var hostBytes = Encoding.ASCII.GetBytes(host + "\0");
        var result = transport._peer.Connect(hostBytes, port, default, null);

        if (result != ConnectionAttemptResult.ConnectionAttemptStarted)
        {
            transport.Dispose();
            throw new InvalidOperationException($"RakNet connect to {host}:{port} failed: {result}");
        }

        return transport;
    }

    public bool Poll(out TransportEvent e)
    {
        Release();

        while (true)
        {
            var packet = _peer.Receive();

            if (packet == null)
            {
                e = default;
                return false;
            }

            if (packet->Length == 0)
            {
                _peer.DeallocatePacket(packet);
                continue;
            }

            var id = packet->Data[0];
            var slot = Find(packet->Guid.G);

            switch ((DefaultMessageIDTypes)id)
            {
                case DefaultMessageIDTypes.NewIncomingConnection:
                case DefaultMessageIDTypes.ConnectionRequestAccepted:
                    slot = Assign(packet->Guid.G, packet->SystemAddress);

                    if (slot < 0)
                    {
                        _peer.CloseConnection(AddressOrGUID.From(packet->Guid), true, 0, PacketPriority.LowPriority);
                        _peer.DeallocatePacket(packet);
                        continue;
                    }

                    _current = packet;
                    e = new TransportEvent(TransportEventKind.Connected, slot, default);
                    return true;

                case DefaultMessageIDTypes.DisconnectionNotification:
                case DefaultMessageIDTypes.ConnectionLost:
                    _current = packet;

                    if (slot >= 0)
                        _guids[slot] = NoGuid;

                    e = new TransportEvent(TransportEventKind.Disconnected, slot, default);
                    return true;

                case DefaultMessageIDTypes.ConnectionAttemptFailed:
                case DefaultMessageIDTypes.NoFreeIncomingConnections:
                case DefaultMessageIDTypes.ConnectionBanned:
                case DefaultMessageIDTypes.InvalidPassword:
                case DefaultMessageIDTypes.IncompatibleProtocolVersion:
                case DefaultMessageIDTypes.IpRecentlyConnected:
                    _current = packet;
                    e = new TransportEvent(TransportEventKind.Failed, -1, default);
                    return true;
            }

            if (id < FirstUserId || slot < 0)
            {
                _peer.DeallocatePacket(packet);
                continue;
            }

            _current = packet;
            _payloadReceived[slot] += packet->Length;
            e = new TransportEvent(TransportEventKind.Data, slot, new ReadOnlySpan<byte>(packet->Data, (int)packet->Length));
            return true;
        }
    }

    public void Send(int connection, ReadOnlySpan<byte> data, Delivery delivery, byte channel)
    {
        if ((uint)connection >= (uint)_guids.Length || _guids[connection] == NoGuid)
            return;

        var reliability = delivery switch
        {
            Delivery.Unreliable => PacketReliability.Unreliable,
            Delivery.UnreliableSequenced => PacketReliability.UnreliableSequenced,
            _ => PacketReliability.ReliableOrdered,
        };

        var target = AddressOrGUID.From(_addresses[connection]);
        _peer.Send(data, PacketPriority.HighPriority, reliability, channel, target, false);
        _payloadSent[connection] += data.Length;
    }

    public void Disconnect(int connection)
    {
        if ((uint)connection >= (uint)_guids.Length || _guids[connection] == NoGuid)
            return;

        _peer.CloseConnection(AddressOrGUID.From(_addresses[connection]), true, 0, PacketPriority.LowPriority);
        _guids[connection] = NoGuid;
    }

    public ConnectionStats Stats(int connection)
    {
        var stats = new ConnectionStats();

        if ((uint)connection >= (uint)_guids.Length)
            return stats;

        stats.PayloadSent = _payloadSent[connection];
        stats.PayloadReceived = _payloadReceived[connection];

        if (_guids[connection] == NoGuid)
            return stats;

        RakNetStatistics raw;

        if (_peer.GetStatistics(_addresses[connection], &raw) == null)
            return stats;

        stats.WireSent = (long)raw.RunningTotal[StatWireSent];
        stats.WireReceived = (long)raw.RunningTotal[StatWireReceived];
        stats.PacketLoss = raw.PacketlossLastSecond;
        stats.RoundTripMs = _peer.GetAveragePing(AddressOrGUID.From(_addresses[connection]));
        return stats;
    }

    /// <summary>RakNet's network simulator on every outgoing packet (if the native build has it).</summary>
    public bool Simulate(float loss, ushort extraPingMs, ushort varianceMs)
    {
        _peer.ApplyNetworkSimulator(loss, extraPingMs, varianceMs);
        return _peer.IsNetworkSimulatorActive();
    }

    public void Dispose()
    {
        if (_peer.IsNull())
            return;

        Release();
        _peer.Shutdown(100);
        _peer.Dispose();
        _peer = default;
    }

    private void Start(uint maxConnections, Span<SocketDescriptor> socket)
    {
        var result = _peer.Startup(maxConnections, socket);

        if (result != StartupResult.RaknetStarted)
        {
            _peer.Dispose();
            throw new InvalidOperationException($"RakNet startup failed: {result}");
        }

        _peer.SetOccasionalPing(true);
    }

    private void Release()
    {
        if (_current == null)
            return;

        _peer.DeallocatePacket(_current);
        _current = null;
    }

    private int Find(ulong guid)
    {
        for (var i = 0; i < _guids.Length; i++)
        {
            if (_guids[i] == guid)
                return i;
        }

        return -1;
    }

    private int Assign(ulong guid, SystemAddress address)
    {
        var slot = Find(guid);

        if (slot < 0)
            slot = Find(NoGuid);

        if (slot < 0)
            return -1;

        _guids[slot] = guid;
        _addresses[slot] = address;
        _payloadSent[slot] = 0;
        _payloadReceived[slot] = 0;
        return slot;
    }
}
