using System.Diagnostics;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.DesignKit.Ui;
using BrutalGame.Engine.Time;
using BrutalGame.Terrain.Runtime;
using BrutalGame.Water.Coast;
using BrutalGame.Water.Ocean;
using BrutalGame.Water.Rendering;

namespace BrutalGame.Debug;

/// <summary>
/// The sea at the probe (the point under the camera): the base swell to edit and apply, GPU detail and foam, the swell
/// and surface readouts, the shore depth maps, the breaking waves' coast regions and the timings. No GC allocation per frame.
/// </summary>
public sealed class OceanPanel : DebugPanel
{
    private const int TimingSamples = 256;
    private const int BenchSide = 32;
    private const int BenchCount = BenchSide * BenchSide;
    private const float BenchSpacing = 4f;
    private const float SliderEms = 9f;

    private readonly OceanSurface _surface;
    private readonly Action<SwellParams> _apply;
    private readonly OceanFft _fft;
    private readonly TimingRing _height = new(TimingSamples);
    private readonly TimingRing _bench = new(TimingSamples);
    private readonly float2[] _benchPoints = new float2[BenchCount];
    private readonly ShoreDepthGrid _depths;
    private readonly ShoreDepthTexture _depthTexture;
    private readonly CoastRegionSet _coast;
    private readonly CoastAtlasGpu _coastGpu;
    private SwellParams _edit;
    private float _fetchKm;
    private float _depth = 50f;
    private bool _deep;
    private bool _benchmark;

    public readonly TimingRing PrepareMs = new(TimingSamples);
    public bool ShoreWash = true;
    public bool BreakerFaces = true;

    /// <summary>Where the readouts and the benchmark sample the sea (planet frame; the sea point under the camera) and the tick they sample it at.</summary>
    public double3 Probe = double3.UnitX;
    public long Tick;

    /// <param name="apply">Sets the sea's base swell (the sim's SetSwell): with weather driving, its wind stays the weather's.</param>
    public OceanPanel(OceanSurface surface, Action<SwellParams> apply, OceanFft fft, ShoreDepthGrid depths, ShoreDepthTexture depthTexture, CoastRegionSet coast, CoastAtlasGpu coastGpu)
        : base("Ocean", GlfwKey.F4, "F4")
    {
        _apply = apply;
        _coast = coast;
        _coastGpu = coastGpu;
        _surface = surface;
        _depths = depths;
        _depthTexture = depthTexture;
        _fft = fft;
        _edit = surface.Swells.Base;
        _fetchKm = _edit.Fetch / 1000f;
        _deep = float.IsPositiveInfinity(_edit.Depth);

        if (!_deep)
            _depth = _edit.Depth;

        const float half = (BenchSide - 1) * BenchSpacing * 0.5f;

        for (var i = 0; i < BenchCount; i++)
            _benchPoints[i] = new float2(i % BenchSide * BenchSpacing - half, i / BenchSide * BenchSpacing - half);
    }

    /// <summary>The swell of the probe's tile.</summary>
    private OceanSwell Swell => _surface.Swells.Near(Probe);

    public void Draw()
    {
        _height.Record(_surface.SampleHeight(Probe, Tick));

        if (_benchmark)
            RunBenchmark();

        if (!Open)
            return;

        if (ImGui.Begin(Title, ref Open))
        {
            if (DebugUi.Section("Swell here"u8))
                DrawReadouts();

            if (DebugUi.Section("Base swell"u8))
                DrawWind();

            if (DebugUi.Section("GPU detail + foam"u8))
                DrawDetail();

            if (DebugUi.Section("Breaking waves"u8))
                DrawCoast();

            if (DebugUi.Section("Shore depth maps"u8))
                DrawDepth();

            if (DebugUi.Section("Perf"u8))
                DrawPerf();
        }

        ImGui.End();
    }

    private void DrawReadouts()
    {
        var swell = Swell;
        var s = _surface.Sample(Probe, Tick);

        if (DebugUi.Props("##swell"u8))
        {
            DebugUi.Row("Tile"u8, $"{_surface.Swells.TileAt(Probe)}  wind {swell.Wind.WindSpeed:F0} m/s");
            DebugUi.Row("Hs / Tp"u8, $"{swell.SignificantWaveHeight:F2} m  {swell.PeakPeriod:F2} s");
            DebugUi.Row("Components"u8, $"{swell.ComponentCount}  choppiness {swell.Choppiness:F3}");
            DebugUi.Row("Sets"u8, $"every {1f / swell.GroupRatio:F0} waves");
            DebugUi.Row("Height"u8, $"{s.Height,7:F3} m  jacobian {s.Jacobian:F3}");
            DebugUi.Row("Normal"u8, $"{s.Normal.X,6:F3} {s.Normal.Y,6:F3} {s.Normal.Z,6:F3}");
            DebugUi.Row("Velocity"u8, $"{s.Velocity.X,6:F3} {s.Velocity.Y,6:F3} {s.Velocity.Z,6:F3} m/s");
            DebugUi.End();
        }

        ImGui.PlotLines("##height"u8, _height.Values, _height.Offset, "height under the camera"u8, float.MaxValue, float.MaxValue, new float2(-1f, DebugUi.Ems(3f)));
        ImGui.ProgressBar(swell.BlendProgress, new float2(-1f, 0f), $"wind blend {100f * swell.BlendProgress:F0}%");
    }

    private void DrawWind()
    {
        DebugUi.Note("The swell every tile starts from; with weather driving, the wind stays the weather's."u8);
        Slider("Wind m/s"u8, ref _edit.WindSpeed, 0f, 40f, "%.1f"u8);
        Angle("Wind direction"u8, ref _edit.WindDirection);
        DesignerStyle.ItemWidth(SliderEms * DesignerStyle.BodySize);
        ImGui.SliderFloat("Fetch km"u8, ref _fetchKm, 1f, 1000f, "%.0f"u8, ImGuiSliderFlags.Logarithmic);
        DebugUi.Tip("Distance the wind has blown over open water: longer fetch, bigger and longer waves"u8);
        ImGui.Checkbox("Deep"u8, ref _deep);

        if (!_deep)
        {
            ImGui.SameLine();
            Slider("Depth m"u8, ref _depth, 1f, 200f, "%.0f"u8);
        }

        Slider("Choppiness"u8, ref _edit.Choppiness, 0f, 1.5f, "%.2f"u8);
        Slider("Spread"u8, ref _edit.Spread, 1f, 30f, "%.1f"u8);
        DebugUi.Tip("Directional spreading exponent: higher is a narrower wind sea"u8);
        Slider("Min wavelength m"u8, ref _edit.MinWavelength, 2f, 50f, "%.1f"u8);
        DebugUi.Tip("Shorter waves are left to the GPU FFT detail"u8);

        ImGui.SeparatorText("Groundswell"u8);
        Slider("Height m"u8, ref _edit.SwellHeight, 0f, 6f, "%.2f"u8);
        Slider("Period s"u8, ref _edit.SwellPeriod, 6f, 22f, "%.1f"u8);
        Angle("Swell direction"u8, ref _edit.SwellDirection);
        Slider("Bandwidth"u8, ref _edit.SwellBandwidth, 0.01f, 0.15f, "%.3f"u8);
        DebugUi.Tip("Frequency bandwidth: narrower gives longer, more regular sets"u8);
        Slider("Swell spread"u8, ref _edit.SwellSpread, 5f, 80f, "%.0f"u8);

        if (ImGui.Button("Apply"u8))
        {
            _edit.Fetch = _fetchKm * 1000f;
            _edit.Depth = _deep ? float.PositiveInfinity : _depth;
            _apply(_edit);
        }

        DebugUi.Tip("Rebuilds every live tile's swell at once (no blend)"u8);
        ImGui.SameLine();

        if (ImGui.Button("Revert"u8))
            _edit = _surface.Swells.Base;
    }

    private void DrawDetail()
    {
        Slider("FFT choppiness"u8, ref _fft.Choppiness, 0f, 2f, "%.2f"u8);
        Slider("Whitecap linger"u8, ref _fft.WhitecapLinger, 1f, 10f, "%.1f"u8);
        Slider("Foam decay /s"u8, ref _fft.FoamDecay, 0f, 3f, "%.2f"u8);
        Slider("Foam strength"u8, ref _fft.FoamStrength, 0f, 2f, "%.2f"u8);
        ImGui.Checkbox("Jacobian debug view"u8, ref _fft.DebugView);
        ImGui.Text($"whitecap coverage {100f * OceanFft.WhitecapCoverage(Swell.Wind.WindSpeed):F1}%");
        DebugUi.Help("Monahan and O'Muircheartaigh (1980), from the wind speed"u8);
    }

    private void DrawCoast()
    {
        ImGui.Checkbox("Breaker faces"u8, ref BreakerFaces);
        ImGui.SameLine();
        ImGui.Checkbox("Shore wash"u8, ref ShoreWash);

        var tile = _surface.Swells.TileAt(Probe);
        var carrier = _coast.BakedFor(tile);

        if (!DebugUi.Props("##coast"u8))
            return;

        if (carrier.Calm)
            DebugUi.Row("Carrier"u8, DesignerStyle.TextDim, $"tile {tile}: calm");
        else
            DebugUi.Row("Carrier"u8, $"tile {tile}: T {MathF.Tau / carrier.Omega:F1} s  H0 {carrier.Height:F2} m  dir {carrier.Direction * 180f / MathF.PI:F0} deg");

        DebugUi.Row("Regions"u8, _coast.Settled ? DebugUi.Good : DebugUi.Warn, $"{_coast.Drawn} / {CoastRegionSet.Capacity} drawn  {(_coast.Settled ? "settled" : "working")}");
        DebugUi.Row("Memory"u8, $"{CoastRegionSet.ResidentBytes / 1048576f:F1} MB resident + {CoastAtlasGpu.Bytes / 1048576f:F1} MB GPU");
        DebugUi.Row("Bakes"u8, $"{_coast.Bakes}  last {_coast.LastBakeMs:F1} ms (depth {_coast.LastBakeDepthMs:F1})  probes {_coast.Probes}");
        DebugUi.Row("Uploads"u8, $"{_coastGpu.Uploads}  last {_coastGpu.LastUploadMs:F3} ms");
        DebugUi.End();
    }

    private void DrawDepth()
    {
        if (!DebugUi.Props("##depth"u8))
            return;

        DebugUi.Row("State"u8, _depths.Settled ? DebugUi.Good : DebugUi.Warn, $"{(_depths.HasMaps ? "maps on" : "no maps")}, {(_depths.Settled ? "settled" : "baking")}, {_depths.Jobs} jobs");
        DebugUi.Row("Last job"u8, $"{_depths.LastJobMs:F2} ms for {_depths.LastJobCells} cells");
        DebugUi.Row("Far bake"u8, $"{_depths.LastFarBakeMs:F1} ms");
        DebugUi.Row("Upload"u8, $"{_depthTexture.LastUploadMs:F3} ms for {_depthTexture.LastUploadCells} cells");
        DebugUi.End();
    }

    private void DrawPerf()
    {
        if (DebugUi.Timings("##ocean-perf"u8))
        {
            DebugUi.Timing("Sea tick (SetTime)"u8, PrepareMs);

            if (_benchmark)
                DebugUi.Timing("Sample benchmark"u8, _bench);

            DebugUi.End();
        }

        ImGui.Checkbox("Benchmark Sample"u8, ref _benchmark);
        DebugUi.Tip($"Samples a {BenchSide} x {BenchSide} grid at {BenchSpacing:F0} m round the probe every frame");

        if (_benchmark)
        {
            ImGui.SameLine();
            ImGui.Text($"{_bench.Average * 1e6f / BenchCount:F1} ns a sample");
        }
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

    private static void Slider(ImString label, ref float value, float min, float max, ImString format)
    {
        DesignerStyle.ItemWidth(SliderEms * DesignerStyle.BodySize);
        ImGui.SliderFloat(label, ref value, min, max, format);
    }

    private static void Angle(ImString label, ref float radians)
    {
        DesignerStyle.ItemWidth(SliderEms * DesignerStyle.BodySize);
        ImGui.SliderAngle(label, ref radians, 0f, 360f);
    }
}
