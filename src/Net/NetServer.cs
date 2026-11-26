using Brutal.Numerics;
using BrutalGame.Net.Transport;
using BrutalGame.Net.Wire;
using BrutalGame.Sim;
using BrutalGame.Sim.Entities;

namespace BrutalGame.Net;

/// <summary>
/// Server side of a session over any <see cref="ITransport"/>: join and leave, tick-stamped inputs for each player's
/// owned entity, 30 Hz delta snapshots per client with interest by frame tree, and reliable events (spawn, despawn,
/// frame change, edit). It drives <see cref="SimWorld.Tick"/> itself so inputs land on the right tick. Main-thread only;
/// <see cref="Poll"/> and <see cref="Step"/> allocate nothing once peers have joined.
/// </summary>
public sealed class NetServer
{
    public const int SnapshotInterval = 2;

    /// <summary>Oldest baseline a delta may use; older ones send full state (the client keeps 32 states per body).</summary>
    public const int MaxBaselineAge = 60;

    /// <summary>Written as the snapshot's input slack before a peer has sent any input.</summary>
    public const int NoSlack = 0x7FFF;

    internal const int InputRing = 128;
    internal const int RecordRing = 32;
    internal const int MaxRecordEntities = 128;

    private readonly SimWorld _sim;
    private readonly ITransport _transport;
    private readonly ServerPeer[] _peers;
    private readonly byte[] _rails;
    private readonly byte[] _snapshot = new byte[Messages.SnapshotBudget + 64];
    private readonly byte[] _event = new byte[512];
    private int _editsSent;

    public NetServer(SimWorld sim, ITransport transport)
    {
        _sim = sim;
        _transport = transport;
        _peers = new ServerPeer[transport.MaxConnections];

        for (var i = 0; i < _peers.Length; i++)
            _peers[i] = new ServerPeer(i, sim.Entities.Capacity);

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream))
            sim.Celestial.Write(writer);

        _rails = stream.ToArray();
        SpawnPlacement = Placement.At(sim.Celestial[0].Inertial, double3.Zero);
    }

    /// <summary>Where players' entities appear; each slot is offset by <see cref="SpawnSpacing"/> along X.</summary>
    public Placement SpawnPlacement;

    public double SpawnSpacing = 20.0;

    public SimWorld Sim => _sim;

    public int PlayerCount { get; private set; }

    public int MaxPlayers => _peers.Length;

    public bool IsJoined(int slot) => _peers[slot].Joined;

    public EntityId PlayerEntity(int slot) => _peers[slot].Entity;

    /// <summary>Snapshot updates sent to <paramref name="slot"/> for the body in entity slot <paramref name="index"/>.</summary>
    internal int UpdatesSent(int slot, int index) => _peers[slot].Updates[index];

    public PeerStats Stats(int slot)
    {
        var peer = _peers[slot];

        return new PeerStats
        {
            Connection = _transport.Stats(slot),
            KnownEntities = peer.KnownCount,
            InputsReceived = peer.InputsReceived,
            InputsLate = peer.InputsLate,
            InputsMissing = peer.InputsMissing,
            Slack = peer.LatestInputTick >= 0 ? peer.LatestInputTick - _sim.Clock.TickIndex : 0,
            SnapshotsSent = peer.SnapshotsSent,
            SnapshotBytes = peer.SnapshotBytes,
            EventBytes = peer.EventBytes,
        };
    }

    /// <summary>Handles every waiting transport event. Call every loop, also while paused with nobody connected.</summary>
    public void Poll()
    {
        while (_transport.Poll(out var e))
        {
            if ((uint)e.Connection >= (uint)_peers.Length)
                continue;

            var peer = _peers[e.Connection];

            switch (e.Kind)
            {
                case TransportEventKind.Connected:
                    peer.Reset();
                    peer.Connected = true;
                    break;

                case TransportEventKind.Disconnected:
                    Leave(peer);
                    break;

                case TransportEventKind.Data:
                    Receive(peer, e.Data);
                    break;
            }
        }
    }

    /// <summary>One sim tick: this tick's inputs onto owned entities, <see cref="SimWorld.Tick"/>, edits, then snapshots every <see cref="SnapshotInterval"/> ticks.</summary>
    public void Step()
    {
        ApplyInputs();
        _sim.Tick();
        BroadcastEdits();

        var tick = _sim.Clock.TickIndex;

        if (tick % SnapshotInterval != 0)
            return;

        foreach (var peer in _peers)
        {
            if (peer.Joined)
                SendSnapshot(peer, tick);
        }
    }

    public void Kick(int slot)
    {
        _transport.Disconnect(slot);
        Leave(_peers[slot]);
    }

    private void Receive(ServerPeer peer, ReadOnlySpan<byte> data)
    {
        var reader = new NetReader(data);

        switch ((MessageId)reader.Byte())
        {
            case MessageId.Hello:
                Join(peer, reader.UShort());
                break;

            case MessageId.Input when peer.Joined:
                ReceiveInput(peer, ref reader);
                break;

            case MessageId.EditRequest when peer.Joined:
                var op = Messages.ReadOp(ref reader);

                if (!reader.Failed && float.IsFinite(op.Radius) && op.Radius is > 0f and <= 16f)
                    _sim.QueueEdit(op with { Seq = 0, Author = (ushort)peer.Slot });

                break;
        }
    }

    private void Join(ServerPeer peer, ushort protocol)
    {
        if (!peer.Connected || peer.Joined)
            return;

        var writer = new NetWriter(_event);

        if (protocol != Messages.Protocol)
        {
            writer.Byte((byte)MessageId.JoinRefused);
            writer.Byte((byte)RefuseReason.Protocol);
            _transport.Send(peer.Slot, writer.Written, Delivery.ReliableOrdered, Channels.Events);
            return;
        }

        var spawn = SpawnPlacement;
        spawn.Position += new double3(peer.Slot * SpawnSpacing, 0.0, 0.0);
        peer.Entity = _sim.Entities.Spawn(EntityKind.Probe, spawn, peer.Slot);
        peer.NetId = _sim.Entities.NetId[peer.Entity.Index];
        peer.Joined = true;
        PlayerCount++;

        var accept = new byte[_rails.Length + 64];
        writer = new NetWriter(accept);
        Messages.JoinAccept(ref writer, peer.Slot, peer.NetId, _sim.Clock.TickIndex, _sim.Clock.TicksPerSecond, _sim.Seed, _rails);
        _transport.Send(peer.Slot, writer.Written, Delivery.ReliableOrdered, Channels.Events);

        var edits = _sim.Edits;

        for (var i = 0; i < edits.Length; i++)
            SendEdit(peer, edits[i]);
    }

    private void Leave(ServerPeer peer)
    {
        if (peer.Joined)
        {
            _sim.Entities.Despawn(peer.Entity);
            PlayerCount--;
        }

        peer.Reset();
    }

    private void ReceiveInput(ServerPeer peer, ref NetReader reader)
    {
        var ack = (long)reader.VarUInt();
        var first = (long)reader.VarUInt();
        var count = reader.Byte();
        var now = _sim.Clock.TickIndex;

        for (var i = 0; i < count; i++)
        {
            var input = Messages.ReadInput(ref reader);
            var tick = first + i;

            if (reader.Failed || tick <= peer.LatestInputTick)
                continue;

            peer.InputsReceived++;

            if (tick < now)
            {
                peer.InputsLate++;
                continue;
            }

            if (tick >= now + InputRing)
                continue;

            var slot = (int)(tick % InputRing);
            peer.Inputs[slot] = input;
            peer.InputTicks[slot] = tick;
            peer.LatestInputTick = tick;
        }

        if (!reader.Failed)
            Ack(peer, ack);
    }

    private void ApplyInputs()
    {
        var tick = _sim.Clock.TickIndex;
        var entities = _sim.Entities;

        foreach (var peer in _peers)
        {
            if (!peer.Joined || !entities.IsAlive(peer.Entity))
                continue;

            var slot = (int)(tick % InputRing);

            if (peer.InputTicks[slot] == tick)
                peer.LastInput = peer.Inputs[slot];
            else if (peer.LatestInputTick >= 0)
                peer.InputsMissing++;

            entities.Input[peer.Entity.Index] = peer.LastInput;
        }
    }

    /// <summary>The client holds snapshot <paramref name="tick"/>: its bodies become the baselines for later deltas.</summary>
    private static void Ack(ServerPeer peer, long tick)
    {
        if (tick <= peer.AckTick)
            return;

        peer.AckTick = tick;
        var record = (int)(tick / SnapshotInterval % RecordRing);

        if (peer.RecordTick[record] != tick)
            return;

        var start = record * MaxRecordEntities;

        for (var i = 0; i < peer.RecordCount[record]; i++)
        {
            var index = peer.RecordIndex[start + i];

            if (peer.Known[index] != peer.RecordNet[start + i] || tick <= peer.AckedTick[index])
                continue;

            peer.Acked[index] = peer.RecordState[start + i];
            peer.AckedTick[index] = tick;
        }
    }

    private void SendSnapshot(ServerPeer peer, long tick)
    {
        var entities = _sim.Entities;
        ForgetDespawned(peer, tick);

        var viewer = entities.IsAlive(peer.Entity) ? entities.Placement[peer.Entity.Index] : SpawnPlacement;
        var writer = new NetWriter(_snapshot);
        writer.Byte((byte)MessageId.Snapshot);
        writer.VarUInt((ulong)tick);
        writer.VarInt(peer.LatestInputTick >= 0 ? Math.Clamp(peer.LatestInputTick - tick, -NoSlack + 1, NoSlack - 1) : NoSlack);
        var countAt = writer.ReserveUShort();

        var record = (int)(tick / SnapshotInterval % RecordRing);
        var recordStart = record * MaxRecordEntities;
        peer.RecordTick[record] = tick;
        var count = 0;

        var alive = entities.Alive;
        var n = alive.Length;
        var start = n > 0 ? peer.Cursor % n : 0;

        for (var k = 0; k < n; k++)
        {
            var index = alive[(start + k) % n];
            var netId = entities.NetId[index];
            ref readonly var placement = ref entities.Placement[index];
            var owned = entities.Owner[index] == peer.Slot;
            var interest = owned ? InterestClass.Owned : Interest.Classify(_sim.Frames, viewer, placement);
            var known = peer.Known[index] == netId;

            if (interest == InterestClass.None)
            {
                if (known)
                    SendDespawn(peer, index, tick);

                continue;
            }

            var state = NetBodyState.From(placement);

            if (!known)
            {
                var spawn = new NetWriter(_event);
                Messages.Spawn(ref spawn, tick, netId, entities.Kind[index], owned, state);
                SendEvent(peer, spawn.Written);
                peer.Remember(index, netId);
                Baseline(peer, index, tick, state);
                continue;
            }

            if (state.Frame != peer.Acked[index].Frame)
            {
                var change = new NetWriter(_event);
                Messages.FrameChange(ref change, tick, netId, state);
                SendEvent(peer, change.Written);
                Baseline(peer, index, tick, state);
                continue;
            }

            if (tick - peer.LastSent[index] < Interest.PeriodTicks(interest))
                continue;

            if (!owned && state.Equals(peer.Acked[index]))
                continue;

            if (count == MaxRecordEntities)
            {
                peer.Cursor = (start + k) % n;
                break;
            }

            var mark = writer.Position;
            var age = tick - peer.AckedTick[index];

            if (age > MaxBaselineAge)
                BodyCodec.WriteFull(ref writer, netId, state);
            else
                BodyCodec.WriteDelta(ref writer, netId, (int)age, state, peer.Acked[index]);

            if (writer.Overflow || writer.Position > Messages.SnapshotBudget)
            {
                writer.Rewind(mark);
                peer.Cursor = (start + k) % n;
                break;
            }

            peer.RecordIndex[recordStart + count] = index;
            peer.RecordNet[recordStart + count] = netId;
            peer.RecordState[recordStart + count] = state;
            peer.LastSent[index] = tick;
            peer.Updates[index]++;
            count++;
        }

        peer.RecordCount[record] = count;
        writer.PatchUShort(countAt, (ushort)count);
        _transport.Send(peer.Slot, writer.Written, Delivery.UnreliableSequenced, Channels.Snapshots);
        peer.SnapshotsSent++;
        peer.SnapshotBytes += writer.Position;
    }

    /// <summary>Despawn events for bodies this peer knows whose slot no longer holds them.</summary>
    private void ForgetDespawned(ServerPeer peer, long tick)
    {
        var netIds = _sim.Entities.NetId;

        for (var i = peer.KnownCount - 1; i >= 0; i--)
        {
            var index = peer.KnownList[i];

            if (netIds[index] != peer.Known[index])
                SendDespawn(peer, index, tick);
        }
    }

    private void SendDespawn(ServerPeer peer, int index, long tick)
    {
        var writer = new NetWriter(_event);
        Messages.Despawn(ref writer, tick, peer.Known[index]);
        SendEvent(peer, writer.Written);
        peer.Forget(index);
    }

    /// <summary>A reliably sent full state is a baseline the client will hold.</summary>
    private static void Baseline(ServerPeer peer, int index, long tick, in NetBodyState state)
    {
        peer.Acked[index] = state;
        peer.AckedTick[index] = tick;
        peer.LastSent[index] = tick;
    }

    private void BroadcastEdits()
    {
        var edits = _sim.Edits;

        for (; _editsSent < edits.Length; _editsSent++)
        {
            foreach (var peer in _peers)
            {
                if (peer.Joined)
                    SendEdit(peer, edits[_editsSent]);
            }
        }
    }

    private void SendEdit(ServerPeer peer, in Terrain.EditOp op)
    {
        var writer = new NetWriter(_event);
        Messages.Edit(ref writer, _sim.Clock.TickIndex, op);
        SendEvent(peer, writer.Written);
    }

    private void SendEvent(ServerPeer peer, ReadOnlySpan<byte> data)
    {
        _transport.Send(peer.Slot, data, Delivery.ReliableOrdered, Channels.Events);
        peer.EventBytes += data.Length;
    }
}

public struct PeerStats
{
    public ConnectionStats Connection;
    public int KnownEntities;
    public long InputsReceived, InputsLate, InputsMissing;
    public long Slack;
    public long SnapshotsSent, SnapshotBytes, EventBytes;
}

/// <summary>Per-client replication state, preallocated per connection slot.</summary>
internal sealed class ServerPeer
{
    public readonly int Slot;

    public readonly EntityInput[] Inputs = new EntityInput[NetServer.InputRing];
    public readonly long[] InputTicks = new long[NetServer.InputRing];

    /// <summary>Net id this peer knows in each entity slot (0 = none), with a dense list of known slots.</summary>
    public readonly uint[] Known;
    public readonly int[] KnownList;
    public readonly int[] KnownSlot;

    public readonly long[] LastSent;
    public readonly int[] Updates;
    public readonly NetBodyState[] Acked;
    public readonly long[] AckedTick;

    public readonly long[] RecordTick = new long[NetServer.RecordRing];
    public readonly int[] RecordCount = new int[NetServer.RecordRing];
    public readonly int[] RecordIndex = new int[NetServer.RecordRing * NetServer.MaxRecordEntities];
    public readonly uint[] RecordNet = new uint[NetServer.RecordRing * NetServer.MaxRecordEntities];
    public readonly NetBodyState[] RecordState = new NetBodyState[NetServer.RecordRing * NetServer.MaxRecordEntities];

    public bool Connected, Joined;
    public EntityId Entity = EntityId.None;
    public uint NetId;
    public EntityInput LastInput;
    public long LatestInputTick = -1;
    public long AckTick = -1;
    public int KnownCount;
    public int Cursor;

    public long InputsReceived, InputsLate, InputsMissing;
    public long SnapshotsSent, SnapshotBytes, EventBytes;

    public ServerPeer(int slot, int entityCapacity)
    {
        Slot = slot;
        Known = new uint[entityCapacity];
        KnownList = new int[entityCapacity];
        KnownSlot = new int[entityCapacity];
        LastSent = new long[entityCapacity];
        Updates = new int[entityCapacity];
        Acked = new NetBodyState[entityCapacity];
        AckedTick = new long[entityCapacity];
        Reset();
    }

    public void Remember(int index, uint netId)
    {
        if (Known[index] == 0)
        {
            KnownSlot[index] = KnownCount;
            KnownList[KnownCount++] = index;
        }

        Known[index] = netId;
    }

    public void Forget(int index)
    {
        if (Known[index] == 0)
            return;

        var slot = KnownSlot[index];
        var last = KnownList[--KnownCount];
        KnownList[slot] = last;
        KnownSlot[last] = slot;
        Known[index] = 0;
    }

    public void Reset()
    {
        for (var i = 0; i < KnownCount; i++)
            Known[KnownList[i]] = 0;

        KnownCount = 0;
        Array.Fill(InputTicks, -1L);
        Array.Fill(RecordTick, -1L);
        Array.Clear(Updates);
        Connected = Joined = false;
        Entity = EntityId.None;
        NetId = 0;
        LastInput = default;
        LatestInputTick = -1;
        AckTick = -1;
        Cursor = 0;
        InputsReceived = InputsLate = InputsMissing = 0;
        SnapshotsSent = SnapshotBytes = EventBytes = 0;
    }
}
