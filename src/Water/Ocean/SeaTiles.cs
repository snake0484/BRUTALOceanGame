using Brutal.Numerics;

namespace BrutalGame.Water.Ocean;

/// <summary>Up to four tiles and their weights at one direction; the weights' squares sum to 1. Tile 0 is the one the direction is in.</summary>
public unsafe struct SeaBlend
{
    public int Count;
    public fixed int Tile[SeaTiles.MaxBlend];
    public fixed float Weight[SeaTiles.MaxBlend];
}

/// <summary>
/// The tiles round the tile corner nearest a direction, for the GPU (<see cref="SeaTiles.Window"/>): the two rows either
/// side of the nearest row edge, and in each the two cells either side of its nearest cell edge. Slot = row side
/// (0 south of the edge, 1 north) + 2 × cell side (0 west, 1 east). A row of one cell (a polar cap) has the same tile on
/// both cell sides.
/// </summary>
public unsafe struct SeaWindow
{
    /// <summary>The row north of the row edge; the row south of it is this − 1.</summary>
    public int EdgeRow;

    /// <summary>Per row side, the cell east of that row's cell edge (the cell west of it is this − 1, wrapped).</summary>
    public fixed int EdgeCell[2];
    public fixed int Tile[SeaTiles.MaxBlend];
}

/// <summary>
/// The sea's global wave frames (review C5): the sea sphere is cut into tiles of about <see cref="TileSize"/>, each
/// with a fixed tangent frame at its centre (east, up, south). A tile's wave field lives in its tangent plane; the point
/// of the sphere at direction d shows the field at (R d·east, R d·south). Within <see cref="BlendHalfWidth"/> of a tile
/// edge the neighbours' fields are blended, the squared weights summing to 1 so the sea keeps its energy. Pure functions
/// of the direction, so every peer agrees.
/// <para>
/// The tiles are rows of latitude about the spin axis, each split into whole cells of longitude (as the weather's
/// lattice, planets.md), with a cap at each pole. Every edge is then a parallel or a meridian: a smooth function of
/// position, which the GPU's linearised copy follows (<see cref="SeaSlots"/>). A cube sphere's tile edges kink by up to
/// 45° where they cross a cube edge and three tiles meet at its corners, which no single plane per edge can follow.
/// </para>
/// Immutable, thread-safe, allocation-free.
/// </summary>
public sealed unsafe class SeaTiles
{
    public const double TileSize = 64_000.0;
    public const double BlendHalfWidth = 2_000.0;

    /// <summary>
    /// One coherent wave cannot be blended as the random sea is: it is handed from tile to tile where their squared
    /// weights are within this of equal (<see cref="Hand"/>), about 270 m either side of the edge.
    /// </summary>
    public const double HandWidth = 0.1;
    public const int MaxBlend = 4;

    public readonly double SeaRadius;

    /// <summary>Rows of latitude, the first and last being the polar caps.</summary>
    public readonly int Rows;

    private readonly double3 _axis;
    private readonly double3 _prime;
    private readonly double3 _quarter;
    private readonly double _rowAngle;
    private readonly double _rowMargin;
    private readonly int[] _cells;
    private readonly int[] _start;
    private readonly double3[] _up;
    private readonly double3[] _east;
    private readonly double3[] _south;

    public SeaTiles(double seaRadius, double3 spinAxis)
    {
        SeaRadius = seaRadius;
        _axis = double3.Normalize(spinAxis);
        _prime = double3.Normalize(double3.Cross(double3.Cross(_axis, Math.Abs(_axis.X) < 0.9 ? double3.UnitX : double3.UnitZ), _axis));
        _quarter = double3.Cross(_axis, _prime);
        Rows = Math.Max(3, (int)Math.Round(Math.PI * seaRadius / TileSize));
        _rowAngle = Math.PI / Rows;
        _rowMargin = BlendHalfWidth / (_rowAngle * seaRadius);
        _cells = new int[Rows];
        _start = new int[Rows + 1];

        for (var row = 0; row < Rows; row++)
        {
            var polar = row == 0 || row == Rows - 1;
            var around = 2.0 * Math.PI * seaRadius * Math.Cos(RowCentre(row));
            _cells[row] = polar ? 1 : Math.Max(1, (int)Math.Round(around / TileSize));
            _start[row + 1] = _start[row] + _cells[row];
        }

        _up = new double3[_start[Rows]];
        _east = new double3[_up.Length];
        _south = new double3[_up.Length];

        for (var row = 0; row < Rows; row++)
        {
            for (var cell = 0; cell < _cells[row]; cell++)
            {
                var tile = _start[row] + cell;
                var latitude = row == 0 ? -0.5 * Math.PI : row == Rows - 1 ? 0.5 * Math.PI : RowCentre(row);
                var longitude = (cell + 0.5) / _cells[row] * Math.Tau;
                var (sinLat, cosLat) = Math.SinCos(latitude);
                var (sinLon, cosLon) = Math.SinCos(longitude);
                _up[tile] = double3.Normalize(_axis * sinLat + (_prime * cosLon + _quarter * sinLon) * cosLat);
                EastSouth(_up[tile], _axis, out _east[tile], out _south[tile]);
            }
        }
    }

    public int Count => _up.Length;

    /// <summary>East and south at <paramref name="up"/> for a body spinning about <paramref name="spinAxis"/>; fixed axes at the poles.</summary>
    public static void EastSouth(in double3 up, in double3 spinAxis, out double3 east, out double3 south)
    {
        var e = double3.Cross(spinAxis, up);

        if (e.LengthSquared() < 1e-12)
            e = double3.Cross(double3.Cross(spinAxis, Math.Abs(spinAxis.X) < 0.9 ? double3.UnitX : double3.UnitZ), up);

        east = double3.Normalize(e);
        south = double3.Cross(east, up);
    }

    public void Axes(int tile, out double3 east, out double3 up, out double3 south)
    {
        east = _east[tile];
        up = _up[tile];
        south = _south[tile];
    }

    /// <summary>The tile-plane coordinates (m) of the sea sphere's point at unit direction <paramref name="dir"/>.</summary>
    public void Plane(int tile, in double3 dir, out double u, out double v)
    {
        u = SeaRadius * double3.Dot(dir, _east[tile]);
        v = SeaRadius * double3.Dot(dir, _south[tile]);
    }

    /// <summary>
    /// The tile's plane axes lifted onto the sphere at <paramref name="dir"/>: the tangents there whose projections on
    /// the tile plane are its east and south. A field vector (a, b) of the tile is a e1 + b e2 on the sea.
    /// </summary>
    public void Lift(int tile, in double3 dir, out double3 e1, out double3 e2)
    {
        var up = _up[tile];
        var along = 1.0 / double3.Dot(up, dir);
        e1 = _east[tile] - up * (double3.Dot(_east[tile], dir) * along);
        e2 = _south[tile] - up * (double3.Dot(_south[tile], dir) * along);
    }

    /// <summary>The tiles whose fields make the sea at unit direction <paramref name="dir"/>, and their weights.</summary>
    public void Locate(in double3 dir, out SeaBlend blend) => Blend(dir, hand: false, out blend);

    /// <summary>
    /// The same tiles with each one's share of a coherent wave there as its weight: the shares sum to 1, and all but a
    /// narrow band at an edge (<see cref="HandWidth"/>) belongs to one tile.
    /// </summary>
    public void Hand(in double3 dir, out SeaBlend blend) => Blend(dir, hand: true, out blend);

    public static double HandShare(double squaredWeight)
    {
        var t = Math.Clamp((squaredWeight - (0.5 - HandWidth)) / (2.0 * HandWidth), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    private void Blend(in double3 dir, bool hand, out SeaBlend blend)
    {
        Angles(dir, out var latitude, out var longitude);
        var t = (latitude + 0.5 * Math.PI) / _rowAngle;
        var row = Math.Clamp((int)t, 0, Rows - 1);
        var f = t - row;
        var share = 1.0;
        var other = -1;

        if (f < _rowMargin && row > 0)
        {
            other = row - 1;
            share = Smooth(0.5 + 0.5 * f / _rowMargin);
        }
        else if (f > 1.0 - _rowMargin && row < Rows - 1)
        {
            other = row + 1;
            share = Smooth(0.5 + 0.5 * (1.0 - f) / _rowMargin);
        }

        if (hand)
            share = HandShare(share);

        blend = default;
        var cosLat = Math.Cos(latitude);
        AddRow(ref blend, row, longitude, cosLat, share, hand);

        if (other >= 0)
            AddRow(ref blend, other, longitude, cosLat, 1.0 - share, hand);
    }

    /// <summary>The four tiles round the tile corner nearest <paramref name="dir"/>.</summary>
    public void Window(in double3 dir, out SeaWindow window)
    {
        Angles(dir, out var latitude, out var longitude);
        window = default;
        window.EdgeRow = Math.Clamp((int)Math.Round((latitude + 0.5 * Math.PI) / _rowAngle), 1, Rows - 1);

        for (var side = 0; side < 2; side++)
        {
            var row = window.EdgeRow - 1 + side;
            var cells = _cells[row];
            var edge = (int)Math.Round(longitude / Math.Tau * cells);
            window.EdgeCell[side] = edge;
            window.Tile[side] = _start[row] + Mod(edge - 1, cells);
            window.Tile[side + 2] = _start[row] + Mod(edge, cells);
        }
    }

    /// <summary>
    /// Signed blend coordinates of <paramref name="dir"/> across the window's row edge and each row's cell edge: −1..1
    /// over the blend (metres from the edge over <see cref="BlendHalfWidth"/>), above 0 north and east. The squared
    /// weight of the higher side is <see cref="Smooth"/>(0.5 + 0.5 × this). A one-cell row has no cell edge: −∞.
    /// </summary>
    public void Band(in SeaWindow window, in double3 dir, out double row, out double south, out double north)
    {
        Angles(dir, out var latitude, out var longitude);
        row = (latitude + 0.5 * Math.PI - window.EdgeRow * _rowAngle) * SeaRadius / BlendHalfWidth;
        var cosLat = Math.Cos(latitude);
        south = CellBand(window.EdgeRow - 1, window.EdgeCell[0], longitude, cosLat);
        north = CellBand(window.EdgeRow, window.EdgeCell[1], longitude, cosLat);
    }

    public static double Smooth(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    /// <summary>The direction at a latitude and longitude (rad) of the tiling.</summary>
    internal double3 Direction(double latitude, double longitude)
    {
        var (sinLat, cosLat) = Math.SinCos(latitude);
        var (sinLon, cosLon) = Math.SinCos(longitude);
        return double3.Normalize(_axis * sinLat + (_prime * cosLon + _quarter * sinLon) * cosLat);
    }

    /// <summary>The latitude (rad) of the edge between row − 1 and row, and how many cells a row has.</summary>
    internal double RowEdge(int row) => row * _rowAngle - 0.5 * Math.PI;

    internal int Cells(int row) => _cells[row];

    private double RowCentre(int row) => (row + 0.5) * _rowAngle - 0.5 * Math.PI;

    private void Angles(in double3 dir, out double latitude, out double longitude)
    {
        latitude = Math.Asin(Math.Clamp(double3.Dot(dir, _axis), -1.0, 1.0));
        longitude = Math.Atan2(double3.Dot(dir, _quarter), double3.Dot(dir, _prime));

        if (longitude < 0.0)
            longitude += Math.Tau;
    }

    /// <summary>The blend's half width as a share of a cell of <paramref name="row"/> at this latitude.</summary>
    private double CellMargin(int row, double cosLat) => Math.Min(0.5, BlendHalfWidth * _cells[row] / (Math.Tau * SeaRadius * Math.Max(cosLat, 1e-9)));

    private double CellBand(int row, int edge, double longitude, double cosLat)
    {
        var cells = _cells[row];

        if (cells == 1)
            return double.NegativeInfinity;

        // The nearest image of the edge: longitude wraps.
        var t = longitude / Math.Tau * cells - edge;
        t -= Math.Round(t / cells) * cells;
        return t / CellMargin(row, cosLat);
    }

    /// <summary>Adds a row's cell at this longitude, and its neighbour across the nearer cell edge inside the blend, sharing <paramref name="share"/>.</summary>
    private void AddRow(ref SeaBlend blend, int row, double longitude, double cosLat, double share, bool hand)
    {
        var cells = _cells[row];
        var t = longitude / Math.Tau * cells;
        var cell = Math.Clamp((int)t, 0, cells - 1);
        var f = t - cell;
        var margin = CellMargin(row, cosLat);
        var own = 1.0;
        var neighbour = -1;

        if (cells > 1 && f < margin)
        {
            neighbour = Mod(cell - 1, cells);
            own = Smooth(0.5 + 0.5 * f / margin);
        }
        else if (cells > 1 && f > 1.0 - margin)
        {
            neighbour = Mod(cell + 1, cells);
            own = Smooth(0.5 + 0.5 * (1.0 - f) / margin);
        }

        if (hand)
            own = HandShare(own);

        var i = blend.Count++;
        blend.Tile[i] = _start[row] + cell;
        blend.Weight[i] = (float)(hand ? share * own : Math.Sqrt(share * own));

        if (neighbour < 0)
            return;

        i = blend.Count++;
        blend.Tile[i] = _start[row] + neighbour;
        blend.Weight[i] = (float)(hand ? share * (1.0 - own) : Math.Sqrt(share * (1.0 - own)));
    }

    private static int Mod(int value, int modulus) => (value % modulus + modulus) % modulus;
}
