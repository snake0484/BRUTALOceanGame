using BrutalGame.Chemistry;
using static BrutalGame.Ships.HullSurface;

namespace BrutalGame.Ships.Components;

/// <summary>Two part ports that meet: the same place, facing each other, the same kind.</summary>
public readonly record struct PortLink(uint PartA, int PortA, uint PartB, int PortB);

/// <summary>What feeds an open pipe end on the test bench (or, later, from the ship): a gas mixture at a pressure and temperature.</summary>
public sealed class Supply
{
    public uint Pipe;

    /// <summary>The pipe's last node (else its first).</summary>
    public bool AtEnd;

    public string Mixture = "mix.methane";
    public double Pressure = 30e5;
    public double Temperature = 290;
}

/// <summary>
/// A component turned into something that runs: parts, pipes and supplies compiled into a <see cref="FlowNetwork"/>.
/// Chambers are gases of their inner volume; a nozzle on a chamber's aft port exhausts it; an injector on a chamber's
/// head port is a one-way orifice from whatever feeds its inlet; a pipe is a gas of its bore volume that bursts above its
/// Barlow rating; an open pipe end is fed by a supply; an igniter on a chamber's igniter port can spark it. Nothing else
/// is assumed: whether it burns is the chemistry data's business.
/// </summary>
public sealed class ComponentModel
{
    public const double PortTolerance = 0.002;

    public readonly FlowNetwork Network;
    public readonly Dictionary<uint, int> ChamberGas = new();
    public readonly Dictionary<uint, int> PipeGas = new();
    public readonly Dictionary<uint, double> PipeRating = new();
    public readonly List<(uint Igniter, int Gas)> Igniters = new();
    public readonly List<string> Issues = new();
    public int Ambient;

    private ComponentModel(ChemistryData chemistry) => Network = new FlowNetwork(chemistry);

    /// <summary>Every pair of ports that meet (positions within <see cref="PortTolerance"/>, facing each other, same kind).</summary>
    public static List<PortLink> Links(ComponentData data)
    {
        var links = new List<PortLink>();
        var ports = new List<(uint Part, int Port, Vector3d P, Vector3d D, PortKind K)>();

        foreach (var part in data.Parts)
        {
            var list = PartGeometry.Ports(part);

            for (var i = 0; i < list.Length; i++)
            {
                var (p, d) = PartGeometry.PortAt(part, i);
                ports.Add((part.Id, i, p, d, list[i].Kind));
            }
        }

        for (var a = 0; a < ports.Count; a++)
        {
            for (var b = a + 1; b < ports.Count; b++)
            {
                var pa = ports[a];
                var pb = ports[b];

                if (pa.Part != pb.Part && pa.K == pb.K && Sub(pa.P, pb.P).Length() < PortTolerance && Dot(pa.D, pb.D) < -0.99)
                    links.Add(new PortLink(pa.Part, pa.Port, pb.Part, pb.Port));
            }
        }

        return links;
    }

    /// <summary>The part and port a given part's port meets, if any.</summary>
    public static bool Partner(List<PortLink> links, uint part, int port, out uint other, out int otherPort)
    {
        foreach (var l in links)
        {
            if (l.PartA == part && l.PortA == port)
            {
                (other, otherPort) = (l.PartB, l.PortB);
                return true;
            }

            if (l.PartB == part && l.PortB == port)
            {
                (other, otherPort) = (l.PartA, l.PortA);
                return true;
            }
        }

        other = 0;
        otherPort = -1;
        return false;
    }

    /// <param name="yieldMpa">Yield strength per plan material index, for pipe ratings.</param>
    /// <param name="ambientPa">Outside pressure (0 = vacuum), air.</param>
    public static ComponentModel Compile(ComponentData data, ChemistryData chemistry, Func<byte, double> yieldMpa, IReadOnlyList<Supply> supplies, double ambientPa)
    {
        var model = new ComponentModel(chemistry);
        var net = model.Network;
        var air = chemistry.Mixture("mix.air");
        var ambient = new Gas(chemistry, 1.0) { Fixed = true };

        if (ambientPa > 0 && air is not null)
            ambient.Fill(air, ambientPa, 288);

        model.Ambient = net.Add("ambient", ambient);
        net.Ambient = model.Ambient;
        var links = Links(data);
        var parts = new Dictionary<uint, ComponentPart>();

        foreach (var p in data.Parts)
            parts[p.Id] = p;

        foreach (var p in data.Parts)
        {
            if (p.Kind != PartKind.Chamber)
                continue;

            double r = p.P0 * 0.0005, length = p.P1 * 0.001;
            var gas = new Gas(chemistry, Math.Max(Math.PI * r * r * length, 1e-6));

            if (ambientPa > 0 && air is not null)
                gas.Fill(air, ambientPa, 288);

            model.ChamberGas[p.Id] = net.Add($"chamber #{p.Id}", gas);
        }

        foreach (var pipe in data.Pipes)
        {
            var rating = pipe.Rating(yieldMpa(pipe.Material));
            model.PipeRating[pipe.Id] = rating;

            if (pipe.InnerMm == 0 || pipe.InnerMm >= pipe.OuterMm)
            {
                model.Issues.Add($"pipe #{pipe.Id}: inner diameter must be below the outer");
                continue;
            }

            var bore = Math.PI * pipe.InnerMetres * pipe.InnerMetres * 0.25;
            var gas = new Gas(chemistry, Math.Max(bore * Math.Max(pipe.Length, 0.01), 1e-9));

            if (ambientPa > 0 && air is not null)
                gas.Fill(air, ambientPa, 288);

            model.PipeGas[pipe.Id] = net.Add($"pipe #{pipe.Id}", gas, rating, bore);
        }

        foreach (var pipe in data.Pipes)
        {
            if (!model.PipeGas.TryGetValue(pipe.Id, out var node))
                continue;

            var bore = Math.PI * pipe.InnerMetres * pipe.InnerMetres * 0.25;
            var cd = 1.0 / Math.Sqrt(1.0 + 0.02 * 0.5 * Math.Max(pipe.Length, 0.01) / Math.Max(pipe.InnerMetres, 1e-4));

            foreach (var (end, atEnd) in new[] { (pipe.From, false), (pipe.To, true) })
            {
                if (end.IsOpen)
                {
                    var supply = supplies.FirstOrDefault(s => s.Pipe == pipe.Id && s.AtEnd == atEnd);
                    var mixture = supply is null ? null : chemistry.Mixture(supply.Mixture);

                    if (supply is null || mixture is null)
                    {
                        model.Issues.Add($"pipe #{pipe.Id}: open {(atEnd ? "end" : "start")} has no supply");
                        continue;
                    }

                    var source = new Gas(chemistry, 1.0);
                    source.Fill(mixture, supply.Pressure, supply.Temperature);
                    source.Fixed = true;
                    var s = net.Add($"supply {mixture.Name}", source);
                    net.Connect(s, node, bore, cd, oneWay: false);
                }
                else if (parts.TryGetValue(end.Part, out var part) && part.Kind == PartKind.Chamber && model.ChamberGas.TryGetValue(part.Id, out var chamber))
                {
                    net.Connect(node, chamber, bore, cd, oneWay: false);
                }

                // An end on an injector inlet feeds through the injector (below).
            }
        }

        foreach (var p in data.Parts)
        {
            switch (p.Kind)
            {
                case PartKind.Nozzle:
                    if (Partner(links, p.Id, 0, out var c, out var cport) && parts[c].Kind == PartKind.Chamber && cport == 0)
                    {
                        var nozzle = PartGeometry.Nozzle(p);

                        if (!nozzle.IsValid)
                            model.Issues.Add($"nozzle #{p.Id}: the bell's wall angles and length do not fit its radii");

                        net.Exhaust(model.ChamberGas[c], model.Ambient, nozzle);
                    }
                    else
                    {
                        model.Issues.Add($"nozzle #{p.Id}: not on a chamber's nozzle port");
                    }

                    break;

                case PartKind.Injector:
                {
                    if (!Partner(links, p.Id, 0, out var target, out _) || parts[target].Kind != PartKind.Chamber)
                    {
                        model.Issues.Add($"injector #{p.Id}: outlet not on a chamber");
                        break;
                    }

                    var feed = data.Pipes.FirstOrDefault(pp => (pp.From.Part == p.Id && pp.From.Port == 1) || (pp.To.Part == p.Id && pp.To.Port == 1));

                    if (feed is null || !model.PipeGas.TryGetValue(feed.Id, out var source))
                    {
                        model.Issues.Add($"injector #{p.Id}: no pipe on its inlet");
                        break;
                    }

                    var area = Math.PI * p.P0 * p.P0 * 0.25e-6;
                    net.Connect(source, model.ChamberGas[target], area, 0.7, oneWay: true);
                    break;
                }

                case PartKind.Igniter:
                    if (Partner(links, p.Id, 0, out var lit, out _) && parts[lit].Kind == PartKind.Chamber)
                        model.Igniters.Add((p.Id, model.ChamberGas[lit]));
                    else
                        model.Issues.Add($"igniter #{p.Id}: not on a chamber's igniter port");

                    break;
            }
        }

        foreach (var (id, _) in model.ChamberGas)
        {
            if (!data.Parts.Any(p => p.Kind == PartKind.Nozzle && Partner(links, p.Id, 0, out var c, out _) && c == id))
                model.Issues.Add($"chamber #{id}: no nozzle");
        }

        return model;
    }

    /// <summary>Sparks every igniter. True if anything lit.</summary>
    public bool Spark()
    {
        var any = false;

        foreach (var (_, gas) in Igniters)
            any |= Network.Spark(gas);

        return any;
    }
}

/// <summary>Pipe surfaces: each straight length a hollow tube (outer and inner wall, annular ends), a ball at each bend.</summary>
public static class PipeGeometry
{
    public static void Surface(PipeRoute pipe, List<SurfacePolygon> output)
    {
        double ro = pipe.OuterMetres * 0.5, ri = Math.Min(pipe.InnerMetres * 0.5, ro * 0.98);

        for (var i = 1; i < pipe.Nodes.Count; i++)
        {
            var a = pipe.Node(i - 1);
            var b = pipe.Node(i);
            var d = Sub(b, a);
            var length = d.Length();

            if (length < 1e-6)
                continue;

            var m = Basis(Scale(d, 1.0 / length));
            var profile = ri > 0
                ? new List<(double R, double Y)> { (ri, 0), (ro, 0), (ro, length), (ri, length) }
                : new List<(double R, double Y)> { (0, 0), (ro, 0), (ro, length), (0, length) };
            PartGeometry.Lathe(profile, m, a, pipe.Material, output);
        }

        // Bends: a ball the pipe's size covers the mitre.
        for (var i = 1; i + 1 < pipe.Nodes.Count; i++)
        {
            // A semicircle from the bottom pole to the top one, counter-clockwise in (r, y).
            var ball = new List<(double R, double Y)>();
            const int rings = 8;

            for (var k = 0; k <= rings; k++)
            {
                var a = -Math.PI / 2 + Math.PI * k / rings;
                ball.Add((Math.Max(Math.Cos(a) * ro, 0.0), Math.Sin(a) * ro));
            }

            PartGeometry.Lathe(ball, [1, 0, 0, 0, 1, 0, 0, 0, 1], pipe.Node(i), pipe.Material, output);
        }
    }

    /// <summary>Metal volume: π (r_o² − r_i²) × length.</summary>
    public static double Volume(PipeRoute pipe)
    {
        double ro = pipe.OuterMetres * 0.5, ri = pipe.InnerMetres * 0.5;
        return Math.PI * (ro * ro - ri * ri) * pipe.Length;
    }

    /// <summary>A rotation taking local +Y to <paramref name="y"/> (row-major, columns are the images of the local axes).</summary>
    private static double[] Basis(Vector3d y)
    {
        var x = Normalise(Cross(Math.Abs(y.Y) < 0.9 ? new Vector3d(0, 1, 0) : new Vector3d(1, 0, 0), y), new Vector3d(1, 0, 0));
        var z = Cross(x, y);
        return [x.X, y.X, z.X, x.Y, y.Y, z.Y, x.Z, y.Z, z.Z];
    }
}
