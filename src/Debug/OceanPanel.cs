using System.Diagnostics;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Engine.Time;
using BrutalGame.Terrain.Runtime;
using BrutalGame.Water.Ocean;
using BrutalGame.Water.Rendering;

namespace BrutalGame.Debug;

/// <summary>
/// CPU swell tuning, readouts and sampling perf, and the shore depth maps' timings. No GC allocation per frame.
/// </summary>
public sealed class OceanPanel
{
    private const int TimingSamples = 256;
    private const int BenchSide = 32;
    private const int BenchCount = BenchSide * BenchSide;
    private const float BenchSpacing = 4f;

    private static readonly float2 GraphSize = new(0f, 48f);

    private readonly OceanSwell _swell;
    private readonly OceanSurface _surface;
    private readonly OceanFft _fft;
    private readonly TimingRing _height = new(TimingSamples);
    private readonly TimingRing _bench = new(TimingSamples);
    private readonly float2[] _benchPoints = new float2[BenchCount];
    private readonly ShoreDepthGrid _depths;
    private readonly ShoreDepthTexture _depthTexture;
    private SwellParams _edit;
    private float _fetchKm;
    private float _depth = 50f;
    private bool _deep;
    private bool _benchmark;

    public readonly TimingRing PrepareMs = new(TimingSamples);
    public bool Open = true;
    public bool ShoreWash = true;

    /// <summary>Where the readouts and the benchmark sample the sea (planet frame; the sea point under the camera) and the tick they sample it at.</summary>
    public double3 Probe = double3.UnitX;
    public long Tick;

    public OceanPanel(OceanSurface surface, OceanFft fft, ShoreDepthGrid depths, ShoreDepthTexture depthTexture)
    {
        _surface = surface;
        _depths = depths;
        _depthTexture = depthTexture;
        _swell = surface.Swell;
        _fft = fft;
        _edit = _swell.Wind;
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
        _height.Record(_surface.SampleHeight(Probe, Tick));

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
            DrawDepth();
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
        ImGui.Checkbox("Shore wash"u8, ref ShoreWash);

        ImGui.SeparatorText("Under the camera"u8);
        ImGui.PlotLines("##height"u8, _height.Values, _height.Offset, default, float.MaxValue, float.MaxValue, GraphSize);

        var s = _surface.Sample(Probe, Tick);
        ImGui.Text($"h {s.Height,7:F3}  jacobian {s.Jacobian:F3}");
        ImGui.Text($"n   {s.Normal.X,7:F3} {s.Normal.Y,7:F3} {s.Normal.Z,7:F3}");
        ImGui.Text($"vel {s.Velocity.X,7:F3} {s.Velocity.Y,7:F3} {s.Velocity.Z,7:F3}");
    }

    private void DrawDepth()
    {
        ImGui.SeparatorText("Shore depth maps"u8);
        ImGui.Text($"{(_depths.HasMaps ? "maps on" : "no maps")}  {(_depths.Settled ? "settled" : "baking")}  jobs {_depths.Jobs}");
        ImGui.Text($"last job {_depths.LastJobMs,6:F2} ms / {_depths.LastJobCells} cells  far bake {_depths.LastFarBakeMs,6:F1} ms");
        ImGui.Text($"upload {_depthTexture.LastUploadMs,6:F3} ms / {_depthTexture.LastUploadCells} cells");
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
        var up = double3.Normalize(Probe);
        SeaTiles.EastSouth(up, double3.UnitY, out var east, out var south);
        var start = Stopwatch.GetTimestamp();

        foreach (var p in _benchPoints)
            _surface.Sample(Probe + east * p.X + south * p.Y, Tick);

        _bench.Record((float)Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }
}
