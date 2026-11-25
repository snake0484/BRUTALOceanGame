namespace BrutalGame.Ships;

/// <summary>Brick key at LOD 0: 8 m bricks of 32 cells at 0.25 m, origin at key × 8 m.</summary>
public readonly record struct BrickKey(short X, short Y, short Z) : IComparable<BrickKey>
{
    public int CompareTo(BrickKey other)
    {
        var c = Z.CompareTo(other.Z);

        if (c != 0)
            return c;

        c = Y.CompareTo(other.Y);
        return c != 0 ? c : X.CompareTo(other.X);
    }
}

/// <summary>
/// Brush binning: which bricks exist (those an Add brush's bounds plus band reach) and, per brick, the indices of every
/// brush whose bounds plus band overlap it, in list order. Bricks are sorted by key. CSR arrays, reused across builds.
/// </summary>
public sealed class HullBins
{
    public const float CellMetres = 0.25f;
    public const int BrickCells = 32;
    public const float BrickMetres = CellMetres * BrickCells;
    public const int MaxBrushesPerBrick = 256;

    /// <summary>Reach beyond a brush's bounds that still changes the clamped density (±127 at 128 per metre), plus the apron cell.</summary>
    public const double Reach = 127.0 / 128.0 + CellMetres;

    private readonly Dictionary<BrickKey, int> _slots = new();
    private BrickKey[] _keys = new BrickKey[64];
    private int[] _counts = new int[64];
    private int[] _start = new int[65];
    private int[] _indices = new int[256];
    private int _brickCount;

    public int BrickCount => _brickCount;

    public ReadOnlySpan<BrickKey> Keys => _keys.AsSpan(0, _brickCount);

    public ReadOnlySpan<int> Brushes(int brick) => _indices.AsSpan(_start[brick], _start[brick + 1] - _start[brick]);

    /// <summary>The most brushes binned to one brick.</summary>
    public int MaxPerBrick { get; private set; }

    public void Build(ReadOnlySpan<HullBrush> brushes)
    {
        _slots.Clear();
        _brickCount = 0;

        // Bricks exist only where Add brushes reach.
        for (var i = 0; i < brushes.Length; i++)
        {
            if (brushes[i].Op != BrushOp.Add)
                continue;

            Range(brushes[i], out var x0, out var y0, out var z0, out var x1, out var y1, out var z1);

            for (var z = z0; z <= z1; z++)
                for (var y = y0; y <= y1; y++)
                    for (var x = x0; x <= x1; x++)
                    {
                        var key = new BrickKey((short)x, (short)y, (short)z);

                        if (!_slots.ContainsKey(key))
                            AddKey(key);
                    }
        }

        Array.Sort(_keys, 0, _brickCount);
        _slots.Clear();

        for (var i = 0; i < _brickCount; i++)
        {
            _slots.Add(_keys[i], i);
            _counts[i] = 0;
        }

        Visit(brushes, fill: false);

        _start[0] = 0;
        MaxPerBrick = 0;

        for (var i = 0; i < _brickCount; i++)
        {
            _start[i + 1] = _start[i] + _counts[i];
            MaxPerBrick = Math.Max(MaxPerBrick, _counts[i]);
            _counts[i] = 0;
        }

        if (_indices.Length < _start[_brickCount])
            _indices = new int[Math.Max(_start[_brickCount], _indices.Length * 2)];

        Visit(brushes, fill: true);
    }

    /// <summary>Inclusive brick range a brush's bounds plus <see cref="Reach"/> overlap.</summary>
    public static void Range(in HullBrush brush, out int x0, out int y0, out int z0, out int x1, out int y1, out int z1)
    {
        BrushGeometry.Bounds(brush, out var min, out var max);
        x0 = BrickOf(min.X - Reach);
        y0 = BrickOf(min.Y - Reach);
        z0 = BrickOf(min.Z - Reach);
        x1 = BrickOf(max.X + Reach);
        y1 = BrickOf(max.Y + Reach);
        z1 = BrickOf(max.Z + Reach);
    }

    private static int BrickOf(double metres) => (int)Math.Floor(metres / BrickMetres);

    private void AddKey(BrickKey key)
    {
        if (_brickCount == _keys.Length)
        {
            Array.Resize(ref _keys, _keys.Length * 2);
            Array.Resize(ref _counts, _keys.Length);
            Array.Resize(ref _start, _keys.Length + 1);
        }

        _slots.Add(key, _brickCount);
        _keys[_brickCount++] = key;
    }

    private void Visit(ReadOnlySpan<HullBrush> brushes, bool fill)
    {
        for (var i = 0; i < brushes.Length; i++)
        {
            Range(brushes[i], out var x0, out var y0, out var z0, out var x1, out var y1, out var z1);

            for (var z = z0; z <= z1; z++)
                for (var y = y0; y <= y1; y++)
                    for (var x = x0; x <= x1; x++)
                    {
                        if (!_slots.TryGetValue(new BrickKey((short)x, (short)y, (short)z), out var slot))
                            continue;

                        if (fill)
                            _indices[_start[slot] + _counts[slot]] = i;

                        _counts[slot]++;
                    }
        }
    }
}
