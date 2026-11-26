using Brutal.Numerics;
using BrutalGame.Frames;
using BrutalGame.Net.Transport;
using BrutalGame.Net.Wire;
using BrutalGame.Sim;
using BrutalGame.Sim.Entities;
using BrutalGame.Terrain;

namespace BrutalGame.Net;

/// <summary>Supplies the owned entity's input for a tick.</summary>
public interface IInputSource
{
    EntityInput Sample(long tick, in Placement owned);
}

/// <summary>
/// Client side of a session: joins, runs its own clock ahead of the server by the input lead, sends tick-stamped
/// inputs, predicts its owned entity with <see cref="ProbeMotion"/>, reconciles it against each snapshot (snap to the
/// server state, replay the inputs since), and interpolates everything else from a per-body buffer. Main-thread only;
/// <see cref="Update"/> allocates nothing after the join.
/// </summary>
public sealed class NetClient
{
    public const int History = 32;
    public const int PredictionRing = 128;

    /// <summary>Remote bodies render this many ticks behind the newest server tick (two 30 Hz snapshots plus jitter), more for slower interest rates.</summary>
    public const int InterpolationDelay = 6;

    public const int MaxExtrapolation = 12;

    /// <summary>Input slack the clock steers for: inputs reach the server this many ticks before they are needed.</summary>
    public const int TargetSlackMin = 2;
    public const int TargetSlackMax = 5;
    public const int ResyncSlack = 30;

    private readonly ITransport _transport;
    private readonly IInputSource _input;
    private readonly byte[] _send = new byte[256];
    private readonly Dictionary<uint, int> _byNet;

    private readonly long[] _historyTick;
    private readonly NetBodyState[] _historyState;
    private readonly int[] _historyHead;
    private readonly int[] _historyCount;

    private readonly EntityInput[] _inputs = new EntityInput[PredictionRing];
    private readonly long[] _inputTicks = new long[PredictionRing];
    private readonly Placement[] _predicted = new Placement[PredictionRing];
    private readonly long[] _predictedTicks = new long[PredictionRing];

    private FixedClock _clock = new(SimWorld.TickRate);
    private double _timeScale = 1.0;
    private double _serverOffset;
    private bool _offsetValid;
    private long _reconciledTick = -1;
    private Placement _owned;

    public NetClient(ITransport transport, IInputSource input, int entityCapacity = EntityTable.DefaultCapacity)
    {
        _transport = transport;
        _input = input;
        Entities = new EntityTable(entityCapacity);
        _byNet = new Dictionary<uint, int>(entityCapacity);
        _historyTick = new long[entityCapacity * History];
        _historyState = new NetBodyState[entityCapacity * History];
        _historyHead = new int[entityCapacity];
        _historyCount = new int[entityCapacity];
        Array.Fill(_inputTicks, -1L);
        Array.Fill(_predictedTicks, -1L);
    }

    /// <summary>Mirror of the server's entities: <see cref="EntityTable.NetId"/> is the server's id, placements are interpolated (remote) or predicted (owned).</summary>
    public readonly EntityTable Entities;

    public readonly List<EditOp> Edits = new(256);

    public ClientStats Stats;

    public bool Connected { get; private set; }

    public bool Joined { get; private set; }

    public bool Failed { get; private set; }

    public int Slot { get; private set; } = -1;

    public uint OwnedNetId { get; private set; }

    public ulong Seed { get; private set; }

    /// <summary>The server's rails, read from the join (world data, never re-derived from the seed).</summary>
    public FrameTree? Frames { get; private set; }

    public CelestialSystem? Celestial { get; private set; }

    public long Tick => _clock.TickIndex;

    public long LatestSnapshotTick { get; private set; } = -1;

    /// <summary>Predicted state of the owned entity at <see cref="Tick"/>.</summary>
    public ref readonly Placement Owned => ref _owned;

    public ConnectionStats Connection => _transport.Stats(0);

    /// <summary>Polls the network, runs the ticks real time allows, then interpolates remote bodies for drawing.</summary>
    public void Update(double frameSeconds)
    {
        Poll();

        if (!Joined)
            return;

        var ticks = _clock.Advance(frameSeconds * _timeScale);

        for (var i = 0; i < ticks; i++)
            RunTick();

        Interpolate();
    }

    public void RequestEdit(in EditOp op)
    {
        var writer = new NetWriter(_send);
        Messages.EditRequest(ref writer, op);
        _transport.Send(0, writer.Written, Delivery.ReliableOrdered, Channels.Events);
    }

    public void Disconnect() => _transport.Disconnect(0);

    private void Poll()
    {
        while (_transport.Poll(out var e))
        {
            switch (e.Kind)
            {
                case TransportEventKind.Connected:
                    Connected = true;
                    var writer = new NetWriter(_send);
                    Messages.Hello(ref writer);
                    _transport.Send(0, writer.Written, Delivery.ReliableOrdered, Channels.Events);
                    break;

                case TransportEventKind.Disconnected:
                    Connected = Joined = false;
                    break;

                case TransportEventKind.Failed:
                    Failed = true;
                    break;

                case TransportEventKind.Data:
                    Receive(e.Data);
                    break;
            }
        }
    }

    private void Receive(ReadOnlySpan<byte> data)
    {
        var reader = new NetReader(data);

        switch ((MessageId)reader.Byte())
        {
            case MessageId.JoinAccept:
                Join(ref reader);
                break;

            case MessageId.JoinRefused:
                Failed = true;
                break;

            case MessageId.Snapshot when Joined:
                ReceiveSnapshot(ref reader);
                break;

            case MessageId.Spawn when Joined:
                ReceiveSpawn(ref reader);
                break;

            case MessageId.Despawn when Joined:
                reader.VarUInt();
                ReceiveDespawn((uint)reader.VarUInt());
                break;

            case MessageId.FrameChange when Joined:
                ReceiveFrameChange(ref reader);
                break;

            case MessageId.Edit when Joined:
                reader.VarUInt();
                var op = Messages.ReadOp(ref reader);

                if (!reader.Failed)
                    Edits.Add(op);

                break;
        }
    }

    private void Join(ref NetReader reader)
    {
        if (reader.UShort() != Messages.Protocol)
        {
            Failed = true;
            return;
        }

        Slot = reader.Byte();
        OwnedNetId = (uint)reader.VarUInt();
        var serverTick = reader.Long();
        var tickRate = reader.Byte();
        Seed = (ulong)reader.Long();
        var rails = reader.Bytes((int)reader.VarUInt());

        if (reader.Failed || tickRate != SimWorld.TickRate)
        {
            Failed = true;
            return;
        }

        Frames = new FrameTree();
        using (var stream = new MemoryStream(rails.ToArray()))
        using (var binary = new BinaryReader(stream))
            Celestial = CelestialSystem.Read(binary, Frames);

        var rtt = Math.Max(0, _transport.Stats(0).RoundTripMs);
        _clock.TickIndex = serverTick + (long)Math.Ceiling(rtt * 0.5 * SimWorld.TickRate / 1000.0) + TargetSlackMax;
        Celestial.Update(_clock.TickIndex);
        Joined = true;
    }

    private void RunTick()
    {
        var tick = _clock.TickIndex;
        var input = _input.Sample(tick, _owned);
        var slot = (int)(tick % PredictionRing);
        _inputs[slot] = input;
        _inputTicks[slot] = tick;

        if (_reconciledTick >= 0)
        {
            ProbeMotion.Step(ref _owned, input, _clock.TickSeconds);
            Remember(tick + 1, _owned);
            WriteOwned();
        }

        _clock.TickIndex++;
        Celestial!.Update(_clock.TickIndex);
        SendInputs(tick);
    }

    private void SendInputs(long newest)
    {
        Span<EntityInput> batch = stackalloc EntityInput[Messages.InputRedundancy];
        var first = newest;
        var count = 0;

        for (var t = newest - Messages.InputRedundancy + 1; t <= newest; t++)
        {
            if (t < 0 || _inputTicks[(int)(t % PredictionRing)] != t)
                continue;

            if (count == 0)
                first = t;

            batch[count++] = _inputs[(int)(t % PredictionRing)];
        }

        var writer = new NetWriter(_send);
        Messages.Input(ref writer, LatestSnapshotTick, first, batch[..count]);
        _transport.Send(0, writer.Written, Delivery.Unreliable, Channels.Inputs);
    }

    private void ReceiveSnapshot(ref NetReader reader)
    {
        var tick = (long)reader.VarUInt();
        var slack = reader.VarInt();
        var count = reader.UShort();

        if (reader.Failed || tick <= LatestSnapshotTick)
            return;

        LatestSnapshotTick = tick;
        Stats.Snapshots++;
        TrackOffset(tick);

        for (var i = 0; i < count; i++)
        {
            BodyCodec.ReadHeader(ref reader, out var netId, out var age);
            var known = _byNet.TryGetValue(netId, out var index);
            var baseline = default(NetBodyState);
            var haveBaseline = age == 0 || known && FindHistory(index, tick - age, out baseline);
            var state = BodyCodec.ReadBody(ref reader, age, baseline);

            if (reader.Failed)
                return;

            if (!known || !haveBaseline)
            {
                Stats.MissingBaselines++;
                continue;
            }

            Push(index, tick, state);

            if (netId == OwnedNetId)
                Reconcile(tick, state);
        }

        SteerClock(slack);
    }

    private void ReceiveSpawn(ref NetReader reader)
    {
        var tick = (long)reader.VarUInt();
        var netId = (uint)reader.VarUInt();
        var kind = (EntityKind)reader.Byte();
        var flags = reader.Byte();
        var state = BodyCodec.ReadState(ref reader);

        if (reader.Failed)
            return;

        if (_byNet.ContainsKey(netId))
            ReceiveDespawn(netId);

        var placement = new Placement { Mode = PlacementMode.Simulated };
        state.To(ref placement);
        var owned = (flags & Messages.SpawnFlagOwned) != 0;
        var id = Entities.Spawn(kind, placement, owned ? Slot : EntityTable.NoOwner, netId);
        _byNet[netId] = id.Index;
        _historyCount[id.Index] = 0;
        Push(id.Index, tick, state);
        Stats.Spawns++;

        if (owned)
            Reconcile(tick, state);
    }

    private void ReceiveDespawn(uint netId)
    {
        if (!_byNet.Remove(netId, out var index))
            return;

        Entities.Despawn(Entities.IdOf(index));
        Stats.Despawns++;
    }

    private void ReceiveFrameChange(ref NetReader reader)
    {
        var tick = (long)reader.VarUInt();
        var netId = (uint)reader.VarUInt();
        var state = BodyCodec.ReadState(ref reader);

        if (reader.Failed || !_byNet.TryGetValue(netId, out var index))
            return;

        Push(index, tick, state);
        Stats.FrameChanges++;

        if (netId == OwnedNetId)
            Reconcile(tick, state);
    }

    /// <summary>
    /// Owned entity: compare the prediction for the server's tick with the server's state (the reconcile error), then snap
    /// to the server state and replay the inputs from that tick to now.
    /// </summary>
    private void Reconcile(long serverTick, in NetBodyState state)
    {
        if (serverTick <= _reconciledTick)
            return;

        var authoritative = new Placement { Mode = PlacementMode.Simulated };
        state.To(ref authoritative);
        var slot = (int)(serverTick % PredictionRing);

        if (_predictedTicks[slot] == serverTick && _predicted[slot].Frame == authoritative.Frame)
        {
            var error = (_predicted[slot].Position - authoritative.Position).Length();
            Stats.ReconcileCount++;
            Stats.ReconcileErrorSum += error;
            Stats.ReconcileErrorMax = Math.Max(Stats.ReconcileErrorMax, error);

            if (error > 0.01)
                Stats.ReconcileOver1Cm++;
        }

        _reconciledTick = serverTick;

        if (serverTick > _clock.TickIndex)
        {
            Stats.ServerAhead++;
            _clock.TickIndex = serverTick + TargetSlackMax;
        }

        _owned = authoritative;
        Remember(serverTick, _owned);

        for (var t = serverTick; t < _clock.TickIndex; t++)
        {
            var inputSlot = (int)(t % PredictionRing);
            var input = _inputTicks[inputSlot] == t ? _inputs[inputSlot] : default;
            ProbeMotion.Step(ref _owned, input, _clock.TickSeconds);
            Remember(t + 1, _owned);
        }

        WriteOwned();
    }

    private void Remember(long tick, in Placement placement)
    {
        var slot = (int)(tick % PredictionRing);
        _predicted[slot] = placement;
        _predictedTicks[slot] = tick;
    }

    private void WriteOwned()
    {
        if (_byNet.TryGetValue(OwnedNetId, out var index))
            Entities.Placement[index] = _owned;
    }

    /// <summary>Client tick minus server tick as snapshots arrive, smoothed: the server's "now" for interpolation.</summary>
    private void TrackOffset(long serverTick)
    {
        var sample = _clock.TickIndex + _clock.Alpha - serverTick;

        if (!_offsetValid)
        {
            _serverOffset = sample;
            _offsetValid = true;
            return;
        }

        _serverOffset += (sample - _serverOffset) * 0.1;
    }

    /// <summary>Runs the clock a little fast or slow until inputs arrive <see cref="TargetSlackMin"/>-<see cref="TargetSlackMax"/> ticks early; jumps if far off.</summary>
    private void SteerClock(long slack)
    {
        if (slack == NetServer.NoSlack)
            return;

        Stats.Slack = slack;

        if (Math.Abs(slack) > ResyncSlack)
        {
            _clock.TickIndex += (TargetSlackMin + TargetSlackMax) / 2 - slack;
            _offsetValid = false;
            Stats.Resyncs++;
            _timeScale = 1.0;
            return;
        }

        _timeScale = slack < TargetSlackMin ? 1.05 : slack > TargetSlackMax ? 0.95 : 1.0;
    }

    private void Push(int index, long tick, in NetBodyState state)
    {
        var count = _historyCount[index];
        var head = _historyHead[index];

        if (count > 0 && _historyTick[index * History + head] >= tick)
        {
            if (_historyTick[index * History + head] == tick)
                _historyState[index * History + head] = state;

            return;
        }

        head = (head + 1) % History;
        _historyHead[index] = head;
        _historyTick[index * History + head] = tick;
        _historyState[index * History + head] = state;
        _historyCount[index] = Math.Min(count + 1, History);
    }

    private bool FindHistory(int index, long tick, out NetBodyState state)
    {
        var head = _historyHead[index];

        for (var i = 0; i < _historyCount[index]; i++)
        {
            var slot = index * History + (head - i + History) % History;

            if (_historyTick[slot] == tick)
            {
                state = _historyState[slot];
                return true;
            }
        }

        state = default;
        return false;
    }

    /// <summary>Remote bodies at the server's estimated now minus their interpolation delay: cubic Hermite on position (with velocities), slerp on rotation.</summary>
    private void Interpolate()
    {
        if (!_offsetValid)
            return;

        var serverNow = _clock.TickIndex + _clock.Alpha - _serverOffset;
        var dt = _clock.TickSeconds;

        foreach (var index in Entities.Alive)
        {
            if (Entities.NetId[index] == OwnedNetId || _historyCount[index] == 0)
                continue;

            var head = _historyHead[index];
            var newest = index * History + head;
            var spacing = _historyCount[index] > 1 ? _historyTick[newest] - _historyTick[index * History + (head - 1 + History) % History] : 2;
            var renderTick = serverNow - Math.Max(InterpolationDelay, 2 * spacing);
            ref var placement = ref Entities.Placement[index];

            if (renderTick >= _historyTick[newest])
            {
                var ahead = Math.Min(renderTick - _historyTick[newest], MaxExtrapolation);
                _historyState[newest].To(ref placement);
                placement.Position += placement.Velocity * (ahead * dt);

                if (renderTick - _historyTick[newest] > 0.5)
                    Stats.Extrapolated++;

                continue;
            }

            var later = newest;
            var found = false;

            for (var i = 1; i < _historyCount[index]; i++)
            {
                var earlier = index * History + (head - i + History) % History;

                if (_historyTick[earlier] <= renderTick)
                {
                    Blend(ref placement, earlier, later, renderTick, dt);
                    found = true;
                    break;
                }

                later = earlier;
            }

            if (!found)
                _historyState[later].To(ref placement);

            Stats.Interpolated++;
        }
    }

    private void Blend(ref Placement placement, int a, int b, double renderTick, double dt)
    {
        ref readonly var from = ref _historyState[a];
        ref readonly var to = ref _historyState[b];

        if (from.Frame != to.Frame)
        {
            to.To(ref placement);
            return;
        }

        var span = _historyTick[b] - _historyTick[a];
        var t = (renderTick - _historyTick[a]) / span;
        var seconds = span * dt;
        var t2 = t * t;
        var t3 = t2 * t;
        var p0 = from.PositionMetres;
        var p1 = to.PositionMetres;
        var v0 = from.Velocity * seconds;
        var v1 = to.Velocity * seconds;

        placement.Frame = to.FrameId;
        placement.Position = p0 * (2 * t3 - 3 * t2 + 1) + v0 * (t3 - 2 * t2 + t) + p1 * (-2 * t3 + 3 * t2) + v1 * (t3 - t2);
        placement.Velocity = double3.Lerp(from.Velocity, to.Velocity, t);
        placement.Rotation = doubleQuat.Slerp(from.RotationQuat, to.RotationQuat, t);
        placement.AngularVelocity = to.AngularVelocity;
    }
}

public struct ClientStats
{
    public long Snapshots, Spawns, Despawns, FrameChanges, MissingBaselines;
    public long ReconcileCount, ReconcileOver1Cm, ServerAhead, Resyncs;
    public double ReconcileErrorSum, ReconcileErrorMax;
    public long Interpolated, Extrapolated;
    public long Slack;

    public readonly double ReconcileErrorMean => ReconcileCount > 0 ? ReconcileErrorSum / ReconcileCount : 0.0;
}
