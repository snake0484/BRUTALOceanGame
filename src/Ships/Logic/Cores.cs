using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BrutalGame.Ships.Logic;

/// <summary>
/// What a function core does (D88). Each kind is code; its numbers (resolution, rate, range, power, cost) are catalog
/// data. Sensors measure and never act; effectors act only through their inputs and report what they did.
/// </summary>
public enum CoreKind : byte
{
    /// <summary>Sensor: a joint's position (the joint's unit: mm on a slider, degrees otherwise).</summary>
    JointPosition,

    /// <summary>Sensor: pressed while its joint is within the core's tolerance of the core's trip position.</summary>
    JointLimit,

    /// <summary>Sensor: something within range inside its cone (Detected) and how far (Range, metres).</summary>
    Proximity,

    /// <summary>Effector: drives its joint to Target at the joint's speed; reports Moving and Jammed (stopped short).</summary>
    JointMotor,

    /// <summary>Effector: On and Brightness (%); reports Lit.</summary>
    Light,

    /// <summary>Sensor: a tool's handle (D91), where it is held; reads the holder's controls: Holding, Trigger, Alt, Mode.</summary>
    Grip,

    /// <summary>Power: stores energy (its capacity) and supplies the component; reports Charge (%) and Draw (W). Empty: no power.</summary>
    Battery,

    /// <summary>Effector: a beam or flame from its facing (cutting torch, welder, mining laser): Fire and Power (%) in; Firing, Heat (%), Overheated out. It heats while firing and cuts out at full heat until cooled to half.</summary>
    Emitter,
}

/// <summary>What a core is for: measuring, acting, or supplying power.</summary>
public enum CoreRole : byte { Sensor, Effector, Power }

/// <summary>What the person holding a tool is doing with it (D91): set by the game from their input, by the bench from its controls.</summary>
public struct HolderInput
{
    public bool Holding;
    public bool Trigger;
    public bool Alt;
    public byte Mode;
}

/// <summary>One signal of a core: in the catalog, its name, unit, resolution (<see cref="Step"/>) and range. Values are whole counts of the step.</summary>
public sealed class CoreSignal
{
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";
    public double Step { get; set; } = 1;
    public double Min { get; set; }
    public double Max { get; set; } = 1;

    /// <summary>An input (set by programmes and consoles); otherwise an output.</summary>
    public bool Input { get; set; }

    public int MinCount => (int)Math.Round(Min / Step);
    public int MaxCount => (int)Math.Round(Max / Step);
}

/// <summary>
/// A catalog core (<c>data/sensors</c>, <c>data/effectors</c>, <c>data/power</c>): its kind, its signals in the kind's order,
/// how often it updates, its power draw and the tick cost it declares. Better parts read finer (smaller steps) and faster.
/// </summary>
public sealed class CoreDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter<CoreKind>))]
    public CoreKind Kind { get; set; }

    public double RateHz { get; set; } = 60;
    public double PowerW { get; set; }
    public int TickCostNs { get; set; }

    /// <summary>Proximity: how far it sees (metres) and the cone's half angle (degrees).</summary>
    public double Range { get; set; }

    public double HalfAngleDeg { get; set; }

    /// <summary>Limit switch: how near its trip position the joint must be (the joint's stored units: mm or 0.1 degree).</summary>
    public short Tolerance { get; set; }

    /// <summary>What it weighs, on top of the solids around it.</summary>
    public double MassKg { get; set; }

    /// <summary>Battery: the energy it holds when full.</summary>
    public double CapacityWh { get; set; }

    /// <summary>Emitter: how fast it heats at full power and cools when off (% of its limit a second).</summary>
    public double HeatPerSecond { get; set; }

    public double CoolPerSecond { get; set; }

    public List<CoreSignal> Signals { get; set; } = new();

    public uint Key => CoreCatalog.Hash(Id);

    public CoreRole Role => Kind switch
    {
        CoreKind.JointPosition or CoreKind.JointLimit or CoreKind.Proximity or CoreKind.Grip => CoreRole.Sensor,
        CoreKind.Battery => CoreRole.Power,
        _ => CoreRole.Effector,
    };

    public bool IsSensor => Role == CoreRole.Sensor;

    public bool NeedsJoint => Kind is CoreKind.JointPosition or CoreKind.JointLimit or CoreKind.JointMotor;

    /// <summary>Ticks between updates (60 a second).</summary>
    public int Period => Math.Max(1, (int)Math.Round(ProgramRunner.TicksPerSecond / Math.Max(0.01, RateHz)));

    /// <summary>The signals each kind has, in order: name and whether it is an input.</summary>
    public static (string Name, bool Input)[] Layout(CoreKind kind) => kind switch
    {
        CoreKind.JointPosition => [("Position", false)],
        CoreKind.JointLimit => [("Pressed", false)],
        CoreKind.Proximity => [("Detected", false), ("Range", false)],
        CoreKind.JointMotor => [("Target", true), ("Moving", false), ("Jammed", false)],
        CoreKind.Light => [("On", true), ("Brightness", true), ("Lit", false)],
        CoreKind.Grip => [("Holding", false), ("Trigger", false), ("Alt", false), ("Mode", false)],
        CoreKind.Battery => [("Charge", false), ("Draw", false)],
        CoreKind.Emitter => [("Fire", true), ("Power", true), ("Firing", false), ("Heat", false), ("Overheated", false)],
        _ => [],
    };

    /// <summary>Whether the signals are the kind's, in its order, each with a positive step and a range.</summary>
    public bool IsValid()
    {
        var layout = Layout(Kind);

        if (Id.Length == 0 || Signals.Count != layout.Length || RateHz <= 0 || PowerW < 0 || TickCostNs < 0 || MassKg < 0 || CapacityWh < 0
            || (Kind == CoreKind.Battery && CapacityWh <= 0))
            return false;

        for (var i = 0; i < layout.Length; i++)
        {
            var s = Signals[i];

            if (!s.Name.Equals(layout[i].Name, StringComparison.OrdinalIgnoreCase) || s.Input != layout[i].Input || s.Step <= 0 || s.Max < s.Min)
                return false;
        }

        return true;
    }
}

/// <summary>The catalog's cores, by key (<see cref="Hash"/> of the id).</summary>
public sealed class CoreCatalog
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, ReadCommentHandling = JsonCommentHandling.Skip };

    private readonly Dictionary<uint, CoreDef> _byKey = new();

    public readonly List<CoreDef> All = new();

    public static uint Hash(string id) => (uint)XxHash64.Hash(Encoding.UTF8.GetBytes(id));

    public bool TryGet(uint key, out CoreDef def) => _byKey.TryGetValue(key, out def!);

    public void Add(CoreDef def)
    {
        if (!def.IsValid() || !_byKey.TryAdd(def.Key, def))
            return;

        All.Add(def);
    }

    /// <summary><c>sensors</c>, <c>effectors</c> and <c>power</c> (<c>*.json</c>) under <paramref name="dataFolder"/>, each folder only its role's kinds.</summary>
    public static CoreCatalog Load(string dataFolder)
    {
        var catalog = new CoreCatalog();
        Read(catalog, Path.Combine(dataFolder, "sensors"), CoreRole.Sensor);
        Read(catalog, Path.Combine(dataFolder, "effectors"), CoreRole.Effector);
        Read(catalog, Path.Combine(dataFolder, "power"), CoreRole.Power);
        return catalog;
    }

    public static CoreDef? FromJson(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<CoreDef>(text, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void Read(CoreCatalog catalog, string folder, CoreRole role)
    {
        if (!Directory.Exists(folder))
            return;

        foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.OrdinalIgnoreCase))
        {
            if (FromJson(File.ReadAllText(file)) is { } def && def.Role == role)
                catalog.Add(def);
        }
    }
}

/// <summary>
/// A core placed in a component: which catalog core, the joint it reads or drives, a limit switch's trip position (the
/// joint's stored units), and where it sits and faces (a proximity sensor's eye, a light), in the component's space.
/// </summary>
public sealed class HullCore
{
    public const int Bytes = 32;

    public uint Id;
    public uint Def;
    public uint Joint;
    public short At;

    /// <summary>Position, 1/1024 m.</summary>
    public int X, Y, Z;

    /// <summary>Facing, a direction scaled to 32767.</summary>
    public short NX, NY = short.MaxValue, NZ;

    public Vector3d Position => new(X * HullSolid.MetresPerPositionUnit, Y * HullSolid.MetresPerPositionUnit, Z * HullSolid.MetresPerPositionUnit);

    public Vector3d Normal => HullSurface.Normalise(new Vector3d(NX, NY, NZ), new Vector3d(0, 1, 0));

    public void SetNormal(Vector3d n)
    {
        n = HullSurface.Normalise(n, new Vector3d(0, 1, 0));
        (NX, NY, NZ) = ((short)Math.Round(n.X * short.MaxValue), (short)Math.Round(n.Y * short.MaxValue), (short)Math.Round(n.Z * short.MaxValue));
    }

    public HullCore Clone() => (HullCore)MemberwiseClone();
}

/// <summary>A component's cores (plan format 17). Edits replace the whole set, so undo restores it exactly.</summary>
public sealed class HullCoreSet
{
    public const int MaxCores = 32;

    public readonly List<HullCore> Cores = new();

    public bool IsEmpty => Cores.Count == 0;

    public int IndexOf(uint id) => Cores.FindIndex(c => c.Id == id);

    public uint NextId() => Cores.Count == 0 ? 1 : Cores.Max(c => c.Id) + 1;

    public HullCoreSet Clone()
    {
        var copy = new HullCoreSet();

        foreach (var c in Cores)
            copy.Cores.Add(c.Clone());

        return copy;
    }

    /// <summary><c>u8 count</c>, per core <c>u32 id, u32 def, u32 joint, i16 at, i32 x, y, z, i16 nx, ny, nz</c> (32 B).</summary>
    public byte[] ToBytes()
    {
        var b = new byte[1 + Cores.Count * HullCore.Bytes];
        b[0] = (byte)Cores.Count;

        for (var i = 0; i < Cores.Count; i++)
        {
            var c = Cores[i];
            var s = b.AsSpan(1 + i * HullCore.Bytes, HullCore.Bytes);
            BinaryPrimitives.WriteUInt32LittleEndian(s, c.Id);
            BinaryPrimitives.WriteUInt32LittleEndian(s[4..], c.Def);
            BinaryPrimitives.WriteUInt32LittleEndian(s[8..], c.Joint);
            BinaryPrimitives.WriteInt16LittleEndian(s[12..], c.At);
            BinaryPrimitives.WriteInt32LittleEndian(s[14..], c.X);
            BinaryPrimitives.WriteInt32LittleEndian(s[18..], c.Y);
            BinaryPrimitives.WriteInt32LittleEndian(s[22..], c.Z);
            BinaryPrimitives.WriteInt16LittleEndian(s[26..], c.NX);
            BinaryPrimitives.WriteInt16LittleEndian(s[28..], c.NY);
            BinaryPrimitives.WriteInt16LittleEndian(s[30..], c.NZ);
        }

        return b;
    }

    public static HullCoreSet? Read(ReadOnlySpan<byte> s, out int consumed)
    {
        consumed = 0;

        if (s.Length < 1 || s[0] > MaxCores || s.Length < 1 + s[0] * HullCore.Bytes)
            return null;

        var set = new HullCoreSet();

        for (var i = 0; i < s[0]; i++)
        {
            var c = s.Slice(1 + i * HullCore.Bytes, HullCore.Bytes);
            set.Cores.Add(new HullCore
            {
                Id = BinaryPrimitives.ReadUInt32LittleEndian(c),
                Def = BinaryPrimitives.ReadUInt32LittleEndian(c[4..]),
                Joint = BinaryPrimitives.ReadUInt32LittleEndian(c[8..]),
                At = BinaryPrimitives.ReadInt16LittleEndian(c[12..]),
                X = BinaryPrimitives.ReadInt32LittleEndian(c[14..]),
                Y = BinaryPrimitives.ReadInt32LittleEndian(c[18..]),
                Z = BinaryPrimitives.ReadInt32LittleEndian(c[22..]),
                NX = BinaryPrimitives.ReadInt16LittleEndian(c[26..]),
                NY = BinaryPrimitives.ReadInt16LittleEndian(c[28..]),
                NZ = BinaryPrimitives.ReadInt16LittleEndian(c[30..]),
            });
        }

        consumed = 1 + s[0] * HullCore.Bytes;
        return set;
    }
}

/// <summary>What a proximity sensor sees: the distance to the nearest thing inside its cone, or infinity. Component space.</summary>
public interface ISensorWorld
{
    double Nearest(Vector3d origin, Vector3d direction, double halfAngleRadians, double range);
}

/// <summary>
/// A component's cores resolved against the catalog and its joints, built once per plan version (never per tick): each
/// core's def, joint index, where its signals sit in the run's bank and its other inputs in the record, its update period
/// and phase (spread by id so cores of one rate do not all sample on the same tick). Cores whose def is missing, or that
/// need a joint the component lacks, are left out (<see cref="Missing"/>).
/// </summary>
public sealed class CoreLayout
{
    public struct Entry
    {
        public uint Id;
        public CoreDef Def;
        public int Joint;
        public int SignalBase;
        public int InputBase;
        public int Period;
        public int Phase;
        public short At;
        public Vector3d Position;
        public Vector3d Normal;
    }

    public readonly List<Entry> Entries = new();

    public int Signals { get; private set; }

    public int Inputs { get; private set; }

    public int Missing { get; private set; }

    public bool HasMotors { get; private set; }

    public bool HasBatteries { get; private set; }

    /// <summary>The cores' own mass, and the batteries' full charge (millijoules).</summary>
    public double MassKg { get; private set; }

    public long CapacityMj { get; private set; }

    public double PowerW { get; private set; }

    public int TickCostNs { get; private set; }

    public int IndexOf(uint id)
    {
        for (var i = 0; i < Entries.Count; i++)
        {
            if (Entries[i].Id == id)
                return i;
        }

        return -1;
    }

    /// <summary>A battery's full charge in millijoules.</summary>
    public static long Capacity(CoreDef def) => (long)Math.Round(def.CapacityWh * 3_600_000.0);

    public static CoreLayout Build(HullCoreSet? set, CoreCatalog catalog, IReadOnlyList<HullJoint> joints)
    {
        var layout = new CoreLayout();

        if (set is null)
            return layout;

        foreach (var core in set.Cores)
        {
            var joint = -1;

            for (var j = 0; j < joints.Count && j < ProgramInstance.MaxJoints; j++)
            {
                if (joints[j].Id == core.Joint)
                    joint = j;
            }

            if (!catalog.TryGet(core.Def, out var def) || (def.NeedsJoint && joint < 0)
                || layout.Signals + def.Signals.Count > ProgramInstance.MaxSignals || layout.Inputs + def.Signals.Count > ProgramInstance.MaxInputs)
            {
                layout.Missing++;
                continue;
            }

            var period = def.Period;
            layout.Entries.Add(new Entry
            {
                Id = core.Id, Def = def, Joint = joint, SignalBase = layout.Signals, InputBase = layout.Inputs, Period = period,
                Phase = (int)(core.Id % (uint)period), At = core.At, Position = core.Position, Normal = core.Normal,
            });
            layout.Signals += def.Signals.Count;

            // A motor's Target is its joint's; other inputs are kept in the record.
            if (def.Kind != CoreKind.JointMotor)
                layout.Inputs += def.Signals.Count(s => s.Input);

            layout.HasMotors |= def.Kind == CoreKind.JointMotor;
            layout.HasBatteries |= def.Kind == CoreKind.Battery;
            layout.MassKg += def.MassKg;
            layout.CapacityMj += Capacity(def);
            layout.PowerW += def.PowerW;
            layout.TickCostNs += def.TickCostNs;
        }

        return layout;
    }
}
