using BrutalGame.Chemistry;
using static BrutalGame.Ships.HullSurface;

namespace BrutalGame.Ships.Components;

/// <summary>A drawn polygon in ship (or component) metres with a normal per corner and a plan material index.</summary>
public sealed class SurfacePolygon
{
    public Vector3d[] Points = [];
    public Vector3d[] Normals = [];
    public byte Material;
}

/// <summary>
/// What each part kind's parameters mean, its ports and its surface. Every part is a solid of revolution about its local
/// +Y axis, drawn from its parameters (closed, wound outward, smooth around the axis and crisp along the profile), so its
/// metal volume, and with the material its mass, comes straight from the surface.
///
/// <list type="bullet">
/// <item>Chamber: P0 inner diameter, P1 length, P2 wall (mm). Spans y ∈ [−L/2, L/2], closed at +Y by a head plate. Ports:
/// 0 nozzle (aft), 1 and 2 injectors (head), 3 igniter (side, spark).</item>
/// <item>Nozzle: P0 throat diameter, P1 exit diameter, P2 bell length (mm), P3 initial and P4 exit wall angle (0.1°), P5 inlet
/// diameter (mm). Inlet at the origin, exhaust towards −Y; a <see cref="BellNozzle"/> contour. Port 0 inlet.</item>
/// <item>Injector: P0 orifice diameter, P1 body diameter, P2 length (mm), P3 feed (0 fuel, 1 oxidiser). One way, inlet (+Y)
/// to outlet (−Y). Ports: 0 outlet, 1 inlet.</item>
/// <item>Igniter: P0 body diameter, P1 length (mm). Port 0 spark tip (−Y).</item>
/// </list>
/// </summary>
public static class PartGeometry
{
    public const int Segments = 40;
    public const double NozzleWallMetres = 0.004;
    public const double ConvergingFactor = 1.2;
    private const double Mm = 0.001;

    public static readonly string[] KindNames = ["Burn chamber", "Nozzle", "Injector", "Igniter"];

    /// <summary>A sensible new part of a kind (a methane–oxygen thruster of about 4 kN at 30 bar supply).</summary>
    public static ComponentPart Default(PartKind kind) => kind switch
    {
        PartKind.Chamber => new ComponentPart { Kind = kind, P0 = 200, P1 = 400, P2 = 8 },
        PartKind.Nozzle => new ComponentPart { Kind = kind, P0 = 48, P1 = 192, P2 = 216, P3 = 300, P4 = 80, P5 = 200 },
        PartKind.Injector => new ComponentPart { Kind = kind, P0 = 24, P1 = 60, P2 = 80, P3 = 0 },
        _ => new ComponentPart { Kind = kind, P0 = 30, P1 = 80 },
    };

    /// <summary>Parameter labels per kind (empty = unused).</summary>
    public static string[] ParameterNames(PartKind kind) => kind switch
    {
        PartKind.Chamber => ["Inner diameter (mm)", "Length (mm)", "Wall (mm)", "", "", ""],
        PartKind.Nozzle => ["Throat diameter (mm)", "Exit diameter (mm)", "Bell length (mm)", "Initial angle (0.1 deg)", "Exit angle (0.1 deg)", "Inlet diameter (mm)"],
        PartKind.Injector => ["Orifice diameter (mm)", "Body diameter (mm)", "Length (mm)", "", "", ""],
        _ => ["Body diameter (mm)", "Length (mm)", "", "", "", ""],
    };

    public static PartPort[] Ports(in ComponentPart p)
    {
        switch (p.Kind)
        {
            case PartKind.Chamber:
            {
                double r = p.P0 * Mm * 0.5, half = p.P1 * Mm * 0.5, w = p.P2 * Mm;
                return
                [
                    new("Nozzle", new Vector3d(0, -half, 0), new Vector3d(0, -1, 0), PortKind.Flow, p.P0 * Mm),
                    new("Injector A", new Vector3d(-r * 0.5, half + w, 0), new Vector3d(0, 1, 0), PortKind.Flow, r * 0.6),
                    new("Injector B", new Vector3d(r * 0.5, half + w, 0), new Vector3d(0, 1, 0), PortKind.Flow, r * 0.6),
                    new("Igniter", new Vector3d(r + w, 0, 0), new Vector3d(1, 0, 0), PortKind.Spark, r * 0.3),
                ];
            }
            case PartKind.Nozzle:
                return [new("Inlet", new Vector3d(0, 0, 0), new Vector3d(0, 1, 0), PortKind.Flow, p.P5 * Mm)];
            case PartKind.Injector:
            {
                var half = p.P2 * Mm * 0.5;
                return
                [
                    new("Outlet", new Vector3d(0, -half, 0), new Vector3d(0, -1, 0), PortKind.Flow, p.P1 * Mm),
                    new("Inlet", new Vector3d(0, half, 0), new Vector3d(0, 1, 0), PortKind.Flow, p.P1 * Mm),
                ];
            }
            default:
                return [new("Spark", new Vector3d(0, -p.P1 * Mm * 0.5, 0), new Vector3d(0, -1, 0), PortKind.Spark, p.P0 * Mm)];
        }
    }

    /// <summary>The nozzle's flow shape from its parameters.</summary>
    public static BellNozzle Nozzle(in ComponentPart p)
    {
        var throat = p.P0 * Mm * 0.5;
        var inlet = Math.Max(p.P5 * Mm * 0.5, throat * 1.05);
        return new BellNozzle
        {
            ThroatRadius = throat,
            ExitRadius = Math.Max(p.P1 * Mm * 0.5, throat),
            ChamberRadius = inlet,
            ConvergingLength = (inlet - throat) * ConvergingFactor,
            BellLength = Math.Max(p.P2 * Mm, 1e-3),
            InitialAngle = p.P3 * Math.PI / 1800.0,
            ExitAngle = p.P4 * Math.PI / 1800.0,
        };
    }

    /// <summary>The closed profile (r, y) the part is revolved from, wound counter-clockwise with r to the right.</summary>
    public static List<(double R, double Y)> Profile(in ComponentPart p)
    {
        var profile = new List<(double, double)>();

        switch (p.Kind)
        {
            case PartKind.Chamber:
            {
                double r = Math.Max(p.P0 * Mm * 0.5, 0.005), half = Math.Max(p.P1 * Mm * 0.5, 0.005), w = Math.Max(p.P2 * Mm, 0.001);
                const double pin = 0.001;
                profile.Add((r, -half));
                profile.Add((r + w, -half));
                profile.Add((r + w, half + w));
                profile.Add((pin, half + w));
                profile.Add((pin, half));
                profile.Add((r, half));
                break;
            }
            case PartKind.Nozzle:
            {
                var nozzle = Nozzle(p);
                var contour = new List<(double X, double R)>();
                nozzle.Contour(contour, 24);
                var lc = nozzle.ConvergingLength;

                // Outer wall from the exit lip up to the inlet, then the inner (flow) wall back down to the exit.
                for (var i = contour.Count - 1; i >= 0; i--)
                    profile.Add((contour[i].R + NozzleWallMetres, -(contour[i].X + lc)));

                for (var i = 0; i < contour.Count; i++)
                    profile.Add((contour[i].R, -(contour[i].X + lc)));

                break;
            }
            case PartKind.Injector:
            {
                double ro = Math.Max(p.P1 * Mm * 0.5, 0.002), ri = Math.Clamp(p.P0 * Mm * 0.5, 0.0005, ro * 0.9), half = Math.Max(p.P2 * Mm * 0.5, 0.002);
                profile.Add((ri, -half));
                profile.Add((ro, -half));
                profile.Add((ro, half));
                profile.Add((ri, half));
                break;
            }
            default:
            {
                double ro = Math.Max(p.P0 * Mm * 0.5, 0.002), half = Math.Max(p.P1 * Mm * 0.5, 0.002), ri = Math.Min(0.001, ro * 0.5);
                profile.Add((ri, -half));
                profile.Add((ro * 0.4, -half));
                profile.Add((ro, -half * 0.6));
                profile.Add((ro, half));
                profile.Add((ri, half));
                break;
            }
        }

        // Counter-clockwise in (r, y): positive signed area.
        var area = 0.0;

        for (var i = 0; i < profile.Count; i++)
        {
            var (ar, ay) = profile[i];
            var (br, by) = profile[(i + 1) % profile.Count];
            area += ar * by - br * ay;
        }

        if (area < 0)
            profile.Reverse();

        return profile;
    }

    /// <summary>The part's surface in component metres (its rotation and position applied), material from the part.</summary>
    public static void Surface(in ComponentPart p, List<SurfacePolygon> output)
    {
        Span<double> m = stackalloc double[9];
        SolidRotation.Matrix(p.Rotation, m);
        var matrix = m.ToArray();
        Lathe(Profile(p), matrix, p.Position, p.Material, output);
    }

    /// <summary>
    /// Revolves a closed counter-clockwise (r, y) profile about local +Y in <see cref="Segments"/> steps. Each profile edge
    /// becomes a ring of quads, wound outward; normals are the edge's normal turned with the angle (smooth around the
    /// axis, crisp at profile corners).
    /// </summary>
    public static void Lathe(List<(double R, double Y)> profile, double[] rotation, Vector3d position, byte material, List<SurfacePolygon> output)
    {
        var cos = new double[Segments + 1];
        var sin = new double[Segments + 1];

        for (var k = 0; k <= Segments; k++)
        {
            var a = k * Math.Tau / Segments;
            cos[k] = k == Segments ? 1.0 : Math.Cos(a);
            sin[k] = k == Segments ? 0.0 : Math.Sin(a);
        }

        for (var i = 0; i < profile.Count; i++)
        {
            var (ar, ay) = profile[i];
            var (br, by) = profile[(i + 1) % profile.Count];
            double dr = br - ar, dy = by - ay;
            var len = Math.Sqrt(dr * dr + dy * dy);

            if (len < 1e-9)
                continue;

            // Outward normal of a counter-clockwise edge in (r, y).
            double nr = dy / len, ny = -dr / len;

            for (var k = 0; k < Segments; k++)
            {
                var a0 = Point(ar, ay, cos[k], sin[k]);
                var a1 = Point(ar, ay, cos[k + 1], sin[k + 1]);
                var b0 = Point(br, by, cos[k], sin[k]);
                var b1 = Point(br, by, cos[k + 1], sin[k + 1]);
                var n0 = new Vector3d(nr * cos[k], ny, nr * sin[k]);
                var n1 = new Vector3d(nr * cos[k + 1], ny, nr * sin[k + 1]);

                // Wound so (b0 − a0) × (a1 − a0) points along the outward normal.
                var poly = new SurfacePolygon
                {
                    Points = [Ship(a0, rotation, position), Ship(b0, rotation, position), Ship(b1, rotation, position), Ship(a1, rotation, position)],
                    Normals = [Turn(n0, rotation), Turn(n0, rotation), Turn(n1, rotation), Turn(n1, rotation)],
                    Material = material,
                };

                var e1 = Sub(poly.Points[1], poly.Points[0]);
                var e2 = Sub(poly.Points[3], poly.Points[0]);

                if (Dot(Cross(e1, e2), poly.Normals[0]) < 0)
                {
                    Array.Reverse(poly.Points);
                    Array.Reverse(poly.Normals);
                }

                // Points on the axis collapse a quad's edge; drop the repeated corner (and the quad, if nothing is left).
                var kept = ar < 1e-9 || br < 1e-9 ? Collapse(poly) : poly;

                if (kept.Points.Length >= 3)
                    output.Add(kept);
            }
        }
    }

    /// <summary>Volume enclosed by closed outward-wound polygons (divergence theorem), m³.</summary>
    public static double Volume(IEnumerable<SurfacePolygon> polygons)
    {
        var volume = 0.0;

        foreach (var poly in polygons)
        {
            for (var i = 1; i + 1 < poly.Points.Length; i++)
                volume += Dot(poly.Points[0], Cross(poly.Points[i], poly.Points[i + 1])) / 6.0;
        }

        return volume;
    }

    /// <summary>A port in component metres: position and direction with the part's rotation and position applied.</summary>
    public static (Vector3d Position, Vector3d Direction) PortAt(in ComponentPart p, int port)
    {
        var ports = Ports(p);
        Span<double> m = stackalloc double[9];
        SolidRotation.Matrix(p.Rotation, m);
        var matrix = m.ToArray();
        return (Ship(ports[port].Position, matrix, p.Position), Turn(ports[port].Direction, matrix));
    }

    private static Vector3d Point(double r, double y, double c, double s) => new(r * c, y, r * s);

    private static Vector3d Ship(Vector3d l, double[] m, Vector3d position) => new(
        position.X + m[0] * l.X + m[1] * l.Y + m[2] * l.Z,
        position.Y + m[3] * l.X + m[4] * l.Y + m[5] * l.Z,
        position.Z + m[6] * l.X + m[7] * l.Y + m[8] * l.Z);

    private static Vector3d Turn(Vector3d v, double[] m) => new(
        m[0] * v.X + m[1] * v.Y + m[2] * v.Z,
        m[3] * v.X + m[4] * v.Y + m[5] * v.Z,
        m[6] * v.X + m[7] * v.Y + m[8] * v.Z);

    private static SurfacePolygon Collapse(SurfacePolygon poly)
    {
        var points = new List<Vector3d>();
        var normals = new List<Vector3d>();

        for (var i = 0; i < poly.Points.Length; i++)
        {
            if (points.Count > 0 && Sub(poly.Points[i], points[^1]).Length() < 1e-12)
                continue;

            points.Add(poly.Points[i]);
            normals.Add(poly.Normals[i]);
        }

        if (points.Count > 1 && Sub(points[0], points[^1]).Length() < 1e-12)
        {
            points.RemoveAt(points.Count - 1);
            normals.RemoveAt(normals.Count - 1);
        }

        return new SurfacePolygon { Points = points.ToArray(), Normals = normals.ToArray(), Material = poly.Material };
    }
}
