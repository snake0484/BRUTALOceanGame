using System.Diagnostics;
using System.Numerics;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Engine.Time;
using BrutalGame.Weather;

namespace BrutalGame.Debug;

/// <summary>
/// Climate tuning, the weather at the camera, and a map of the weather around it (Z down, so the view is from above):
/// cloud as grey, rain as blue, storm cores red, wind arrows, each pressure system's extent, the camera in white.
/// The map is shaded from the cached snapshots only (cells off them drawn as no data) and re-shaded when the snapshots,
/// the map's cell or a step of their blend changes. No GC allocation per frame.
/// </summary>
public sealed class WeatherPanel
{
    private const int TimingSamples = 256;
    private const float CellPixels = 8f;
    private const int ArrowEvery = 4;
    private const float ArrowPixelsPerMetrePerSecond = 0.8f;
    private const int ArrowSide = WeatherMap.Size / ArrowEvery;

    /// <summary>Re-shades per snapshot interval as the blend between the pair moves on.</summary>
    private const int BlendSteps = 8;

    private static readonly float2 GraphSize = new(0f, 48f);
    private static readonly ImColor8 NoData = new(40, 40, 46, 255);

    private readonly WeatherMap _map;
    private readonly ImColor8[] _shades = new ImColor8[WeatherMap.Size * WeatherMap.Size];
    private readonly Vector2[] _arrows = new Vector2[ArrowSide * ArrowSide];
    private readonly bool[] _arrowValid = new bool[ArrowSide * ArrowSide];
    private WeatherClimate _edit;
    private int _seed;
    private int _shadedVersion = -1;
    private long _shadedStep;
    private Vector2 _shadedOrigin;
    private int _noDataCells;

    public readonly TimingRing TickMs = new(TimingSamples);
    public readonly TimingRing MapMs = new(TimingSamples);
    public bool Open = true;

    /// <summary>The old map: every cell sampled every frame, the analytic field filling cells off the snapshots.</summary>
    public bool AnalyticMap;
    public bool DriveOcean = true;
    public bool DriveSky = true;

    /// <summary>Debug offset of weather time from sim time, to scrub through systems' lives.</summary>
    public float TimeOffsetMinutes;

    /// <summary>Rain drawn at <see cref="RainOverride"/> whatever the weather (--rain).</summary>
    public bool OverrideRain;
    public float RainOverride = 0.6f;
    public int RainDrops;

    public WeatherPanel(WeatherMap map)
    {
        _map = map;
        _edit = map.Field.Climate;
        _seed = (int)_edit.Seed;
    }

    public double TimeOffset => TimeOffsetMinutes * 60.0;

    public void Draw(Vector2 camera)
    {
        if (!Open)
            return;

        ImGui.SetNextWindowPos(new float2(796f, 12f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new float2(300f, 0f), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Weather"u8, ref Open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            DrawClimate();
            DrawLocal(camera);
            DrawMap(camera);
            DrawPerf();
        }

        ImGui.End();
    }

    private void DrawClimate()
    {
        ImGui.SeparatorText("Climate"u8);
        ImGui.SliderFloat("Prevailing m/s"u8, ref _edit.PrevailingSpeed, 0f, 15f, "%.1f"u8);
        ImGui.SliderAngle("Toward"u8, ref _edit.PrevailingDirection, 0f, 360f);
        ImGui.SliderFloat("Storminess"u8, ref _edit.Storminess, 0f, 1f, "%.2f"u8);
        ImGui.SliderFloat("Base cloud"u8, ref _edit.BaseCloud, 0f, 0.6f, "%.2f"u8);
        ImGui.InputInt("Seed"u8, ref _seed);

        if (ImGui.Button("Apply climate"u8))
        {
            _edit.Seed = (ulong)_seed;
            _map.Field.Climate = _edit;
            _map.Invalidate();
        }

        ImGui.SliderFloat("Time offset min"u8, ref TimeOffsetMinutes, 0f, 600f, "%.0f"u8);
        ImGui.Checkbox("Drive ocean wind"u8, ref DriveOcean);
        ImGui.SameLine();
        ImGui.Checkbox("Drive sky + rain"u8, ref DriveSky);
        ImGui.Checkbox("Override rain"u8, ref OverrideRain);
        ImGui.SameLine();
        ImGui.SliderFloat("##rain"u8, ref RainOverride, 0f, 1f, "%.2f"u8);
    }

    private void DrawLocal(Vector2 camera)
    {
        var s = _map.Sample(camera.X, camera.Y);
        var toward = s.WindDirection * 180f / MathF.PI;

        ImGui.SeparatorText("At camera"u8);
        ImGui.Text($"wind {s.WindSpeed:F1} m/s toward {(toward < 0f ? toward + 360f : toward):F0} deg  gusts x{1f + s.Gust:F2}");
        ImGui.Text($"cloud {100f * s.Cloud:F0}%  rain {100f * s.Rain:F0}%  storm {100f * s.Storm:F0}%  {s.Pressure:F1} hPa");
        ImGui.Text($"t {_map.Time / 60.0:F1} min  {_map.Systems.Length} systems in range");
    }

    private void DrawMap(Vector2 camera)
    {
        const float side = WeatherMap.Size * CellPixels;
        const float half = WeatherMap.Size / 2 * WeatherMap.Spacing;
        const float pixelsPerMetre = CellPixels / WeatherMap.Spacing;

        var origin = new Vector2(MathF.Floor(camera.X / WeatherMap.Spacing) * WeatherMap.Spacing - half, MathF.Floor(camera.Y / WeatherMap.Spacing) * WeatherMap.Spacing - half);
        var start = Stopwatch.GetTimestamp();
        var step = (long)Math.Floor(_map.Time / WeatherMap.Interval * BlendSteps);

        if (AnalyticMap || _shadedVersion != _map.Version || step != _shadedStep || origin != _shadedOrigin)
        {
            ShadeMap(origin);
            _shadedVersion = _map.Version;
            _shadedStep = step;
            _shadedOrigin = origin;
        }

        ImGui.SeparatorText("Map (32 km)"u8);
        var corner = ImGui.GetCursorScreenPos();
        var list = ImGui.GetWindowDrawList();
        ImGui.Dummy(new float2(side, side));
        list.PushClipRect(corner, corner + new float2(side, side), true);

        for (var j = 0; j < WeatherMap.Size; j++)
        {
            for (var i = 0; i < WeatherMap.Size; i++)
            {
                var min = corner + new float2(i * CellPixels, j * CellPixels);
                list.AddRectFilled(min, min + new float2(CellPixels, CellPixels), _shades[j * WeatherMap.Size + i]);
            }
        }

        for (var a = 0; a < _arrows.Length; a++)
        {
            if (!_arrowValid[a])
                continue;

            var wind = _arrows[a];
            var tail = corner + new float2((a % ArrowSide * ArrowEvery + ArrowEvery / 2) * CellPixels, (a / ArrowSide * ArrowEvery + ArrowEvery / 2) * CellPixels);
            var head = tail + new float2(wind.X, wind.Y) * ArrowPixelsPerMetrePerSecond;
            list.AddLine(tail, head, new ImColor8(255, 255, 255, 200), 1.5f);
            list.AddCircleFilled(head, 2f, new ImColor8(255, 255, 255, 200), 6);
        }

        foreach (ref readonly var system in _map.Systems)
        {
            var centre = corner + Pixels(system.Centre - origin, pixelsPerMetre);
            list.AddCircle(centre, system.Radius * pixelsPerMetre, KindColour(system.Kind, system.Strength), 32, 1.5f);
        }

        list.AddCircleFilled(corner + Pixels(camera - origin, pixelsPerMetre), 3.5f, new ImColor8(255, 255, 255, 255), 12);
        list.PopClipRect();

        if (ImGui.Checkbox("Analytic map (slow)"u8, ref AnalyticMap))
            _shadedVersion = -1;

        MapMs.Record((float)Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    }

    /// <summary>Shades every cell (sampled at its centre) and the wind arrows from the snapshots; cells off them are no data.</summary>
    private void ShadeMap(Vector2 origin)
    {
        _noDataCells = 0;

        for (var j = 0; j < WeatherMap.Size; j++)
        {
            for (var i = 0; i < WeatherMap.Size; i++)
            {
                var found = SampleMap(origin.X + (i + 0.5f) * WeatherMap.Spacing, origin.Y + (j + 0.5f) * WeatherMap.Spacing, out var s);
                _shades[j * WeatherMap.Size + i] = found ? Shade(s) : NoData;

                if (!found)
                    _noDataCells++;
            }
        }

        for (var a = 0; a < _arrows.Length; a++)
        {
            var i = a % ArrowSide * ArrowEvery + ArrowEvery / 2;
            var j = a / ArrowSide * ArrowEvery + ArrowEvery / 2;
            _arrowValid[a] = SampleMap(origin.X + i * WeatherMap.Spacing, origin.Y + j * WeatherMap.Spacing, out var s);
            _arrows[a] = s.Wind;
        }
    }

    private bool SampleMap(float x, float z, out WeatherSample sample)
    {
        if (!AnalyticMap)
            return _map.TrySample(x, z, out sample);

        sample = _map.Sample(x, z);
        return true;
    }

    private void DrawPerf()
    {
        ImGui.SeparatorText("Perf"u8);
        ImGui.Text($"Tick     {TickMs.Average,7:F4} ms  max {TickMs.Max,7:F4}");
        ImGui.Text($"Rebuild  {_map.LastRebuildMs,7:F3} ms  ({_map.Rebuilds} rebuilds)");
        ImGui.Text($"Map      {MapMs.Average,7:F4} ms  max {MapMs.Max,7:F4}  ({_noDataCells} cells no data)");
        ImGui.Text($"Rain     {RainDrops} drops (GPU timings in the profiler)");
        ImGui.PlotLines("##weather"u8, TickMs.Values, TickMs.Offset, default, 0f, MathF.Max(TickMs.Max, 0.01f), GraphSize);
    }

    private static float2 Pixels(Vector2 metres, float pixelsPerMetre) => new(metres.X * pixelsPerMetre, metres.Y * pixelsPerMetre);

    /// <summary>Clear sky blue to overcast grey, darkened toward blue by rain and toward red in storm cores.</summary>
    private static ImColor8 Shade(in WeatherSample s)
    {
        var r = float.Lerp(70f, 205f, s.Cloud);
        var g = float.Lerp(125f, 208f, s.Cloud);
        var b = float.Lerp(200f, 215f, s.Cloud);
        var wet = 1f - 0.6f * s.Rain;
        r = float.Lerp(r * wet, 230f, s.Storm);
        g *= wet * (1f - 0.7f * s.Storm);
        b *= 1f - 0.4f * s.Storm;
        return new ImColor8((byte)r, (byte)g, (byte)b, 255);
    }

    private static ImColor8 KindColour(PressureKind kind, float strength)
    {
        var alpha = (byte)(80f + 175f * strength);
        return kind switch
        {
            PressureKind.Storm => new ImColor8(255, 60, 60, alpha),
            PressureKind.Low => new ImColor8(80, 140, 255, alpha),
            _ => new ImColor8(255, 220, 80, alpha),
        };
    }
}
