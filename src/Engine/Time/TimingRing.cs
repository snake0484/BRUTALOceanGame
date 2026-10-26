namespace BrutalGame.Engine.Time;

/// <summary>
/// Fixed-size ring of timings in milliseconds. No allocation after construction.
/// </summary>
public sealed class TimingRing
{
    public readonly int Capacity;
    private readonly float[] _values;
    private int _head;
    private int _count;

    public TimingRing(int capacity)
    {
        Capacity = capacity;
        _values = new float[capacity];
    }

    public ReadOnlySpan<float> Values => _values.AsSpan(0, _count);

    /// <summary>
    /// Index of the oldest sample in <see cref="Values"/>, for plotting the ring in order.
    /// </summary>
    public int Offset => _count < Capacity ? 0 : _head;

    public float Last => _count == 0 ? 0f : _values[(_head + Capacity - 1) % Capacity];

    public float Average
    {
        get
        {
            if (_count == 0)
                return 0f;

            var sum = 0f;
            foreach (var v in Values)
                sum += v;

            return sum / _count;
        }
    }

    public float Max
    {
        get
        {
            var max = 0f;
            foreach (var v in Values)
                max = MathF.Max(max, v);

            return max;
        }
    }

    public void Record(float ms)
    {
        _values[_head] = ms;
        _head = (_head + 1) % Capacity;

        if (_count < Capacity)
            _count++;
    }
}
