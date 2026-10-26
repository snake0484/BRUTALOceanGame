using System.Diagnostics;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Engine.Time;
using BrutalGame.Water.Ocean;
using BrutalGame.Water.Rendering;

namespace BrutalGame.Debug;

/// <summary>
/// CPU swell tuning, readouts and sampling perf. No GC allocation per frame.
/// </summary>
public sealed class OceanPanel
{
    private const int TimingSamples = 256;
    private const int BenchSide = 32;
    private const int BenchCount = BenchSide * BenchSide;
    private const float BenchSpacing = 4f;

    private static readonly float2 GraphSize = new(0f, 48f);

    private readonly OceanSwell _swell;
    private readonly OceanFft _fft;
    private readonly TimingRing _height = new(TimingSamples);
    private readonly TimingRing _bench = new(TimingSamples);
    private readonly float2[] _benchPoints = new float2[BenchCount];
    private SwellParams _edit;
    private float _fetchKm;
    private float _depth = 50f;
    private bool _deep;
    private bool _benchmark;

    public readonly TimingRing PrepareMs = new(TimingSamples);
    public bool Open = true;
    public bool CoastalEnabled = true;
    public float CoastalBuildMs;
    public int CoastalBuilds;
    public bool ShowBreakers;
    public bool ShowLips;
    public bool FoldLip;

    /// <summary>Lips seeded by the chained crest polylines (the old seeding) instead of the raw crest crossings, for A/B comparison (BreakerFaces.UseChains).</summary>
    public bool ChainLips;
    public int BreakerCount;
    public int SurfTexels;
    public readonly TimingRing BreakerMs = new(TimingSamples);

    public OceanPanel(OceanSwell swell, OceanFft fft)
    {
        _swell = swell;
        _fft = fft;
        _edit = swell.Wind;
        _fetchKm = _edit.Fetch / 1000f;
        _deep = float.IsPositiveInfinity(_edit.Depth);

        if (!_deep)
            _depth = _edit.Depth;

        const float half = (BenchSide - 1) * BenchSpacing * 0.5f;
        for (var i = 0; i < BenchCount; i++)
            _benchPoints[i] = new float2(i % BenchSide * BenchSpacing - half, i / BenchSide * BenchSpacing - half);
    }

    public void Draw()
    {
        _height.Record(_swell.SampleHeight(0f, 0f));

        if (_benchmark)
            RunBenchmark();

        if (!Open)
            return;

        ImGui.SetNextWindowPos(new float2(404f, 12f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new float2(380f, 0f), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Ocean"u8, ref Open))
        {
            DrawWind();
            DrawDetail();
            DrawReadouts();
            DrawPerf();
        }

        ImGui.End();
    }

    private void DrawWind()
    {
        ImGui.SeparatorText("Wind"u8);
        ImGui.SliderFloat("Speed m/s"u8, ref _edit.WindSpeed, 0f, 40f, "%.1f"u8);
        ImGui.SliderAngle("Direction"u8, ref _edit.WindDirection, 0f, 360f);
        ImGui.SliderFloat("Fetch km"u8, ref _fetchKm, 1f, 1000f, "%.0f"u8, ImGuiSliderFlags.Logarithmic);
        ImGui.Checkbox("Deep"u8, ref _deep);

        if (!_deep)
        {
            ImGui.SameLine();
            ImGui.SliderFloat("Depth m"u8, ref _depth, 1f, 200f, "%.0f"u8);
        }

        ImGui.SliderFloat("Choppiness"u8, ref _edit.Choppiness, 0f, 1.5f, "%.2f"u8);
        ImGui.SliderFloat("Spread"u8, ref _edit.Spread, 1f, 30f, "%.1f"u8);
        ImGui.SliderFloat("Min wavelength m"u8, ref _edit.MinWavelength, 2f, 50f, "%.1f"u8);

        ImGui.SeparatorText("Groundswell"u8);
        ImGui.SliderFloat("Height m"u8, ref _edit.SwellHeight, 0f, 6f, "%.2f"u8);
        ImGui.SliderFloat("Period s"u8, ref _edit.SwellPeriod, 6f, 22f, "%.1f"u8);
        ImGui.SliderAngle("Swell direction"u8, ref _edit.SwellDirection, 0f, 360f);
        ImGui.SliderFloat("Bandwidth"u8, ref _edit.SwellBandwidth, 0.01f, 0.15f, "%.3f"u8);
        ImGui.SliderFloat("Swell spread"u8, ref _edit.SwellSpread, 5f, 80f, "%.0f"u8);
        ImGui.Text($"sets every {1f / _swell.GroupRatio:F0} waves");

        if (ImGui.Button("Apply wind"u8))
        {
            _edit.Fetch = _fetchKm * 1000f;
            _edit.Depth = _deep ? float.PositiveInfinity : _depth;
            _swell.SetWind(_edit);
        }

        ImGui.SameLine();
        ImGui.ProgressBar(_swell.BlendProgress, new float2(-1f, 0f), "blend"u8);
    }

    private void DrawDetail()
    {
        ImGui.SeparatorText("GPU detail + foam"u8);
        ImGui.SliderFloat("FFT choppiness"u8, ref _fft.Choppiness, 0f, 2f, "%.2f"u8);
        ImGui.Text($"Whitecap coverage {100f * OceanFft.WhitecapCoverage(_swell.Wind.WindSpeed):F1} % (Monahan & O'Muircheartaigh 1980)");
        ImGui.SliderFloat("Whitecap linger"u8, ref _fft.WhitecapLinger, 1f, 10f, "%.1f"u8);
        ImGui.SliderFloat("Foam decay /s"u8, ref _fft.FoamDecay, 0f, 3f, "%.2f"u8);
        ImGui.SliderFloat("Foam strength"u8, ref _fft.FoamStrength, 0f, 2f, "%.2f"u8);
        ImGui.Checkbox("Jacobian debug view"u8, ref _fft.DebugView);
    }

    private void DrawReadouts()
    {
        ImGui.SeparatorText("Swell"u8);
        ImGui.Text($"Hs {_swell.SignificantWaveHeight:F2} m  Tp {_swell.PeakPeriod:F2} s  components {_swell.ComponentCount}");
        ImGui.Text($"choppiness {_swell.Choppiness:F3}  t {_swell.Time:F2} s");
        ImGui.Checkbox("Layer B coastal", ref CoastalEnabled);
        ImGui.SameLine();
        ImGui.Text($"{CoastalBuilds} builds  last {CoastalBuildMs:F1} ms (jobs)");
        ImGui.Checkbox("Breakers", ref ShowBreakers);
        ImGui.SameLine();
        ImGui.Checkbox("Lips", ref ShowLips);
        ImGui.SameLine();
        ImGui.Checkbox("Fold lip"u8, ref FoldLip);
        ImGui.Checkbox("Chain seeds"u8, ref ChainLips);
        ImGui.SameLine();
        ImGui.Text($"{BreakerCount} crests  {SurfTexels} surf texels  {BreakerMs.Average:F3} ms  max {BreakerMs.Max:F3}");

        ImGui.SeparatorText("Origin"u8);
        ImGui.PlotLines("##height"u8, _height.Values, _height.Offset, default, float.MaxValue, float.MaxValue, GraphSize);

        var s = _swell.Sample(0f, 0f);
        ImGui.Text($"h {s.Height,7:F3}  jacobian {s.Jacobian:F3}");
        ImGui.Text($"n   {s.Normal.X,7:F3} {s.Normal.Y,7:F3} {s.Normal.Z,7:F3}");
        ImGui.Text($"vel {s.Velocity.X,7:F3} {s.Velocity.Y,7:F3} {s.Velocity.Z,7:F3}");
    }

    private void DrawPerf()
    {
        ImGui.SeparatorText("Perf"u8);
        ImGui.Text($"SetTime   {PrepareMs.Average,7:F4} ms  max {PrepareMs.Max,7:F4}");
        ImGui.Checkbox("Benchmark"u8, ref _benchmark);

        if (!_benchmark)
            return;

        var avg = _bench.Average;
        ImGui.Text($"{BenchCount} samples  {avg,7:F4} ms  {avg * 1e6f / BenchCount,6:F1} ns/sample");
    }

    private void RunBenchmark()
    {
        var start = Stopwatch.GetTimestamp();
        foreach (var p in _benchPoints)
            _swell.Sample(p.X, p.Y);

        _bench.Record((float)Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }
}
