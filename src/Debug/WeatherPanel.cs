using System.Numerics;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Engine.Time;
using BrutalGame.Weather;

namespace BrutalGame.Debug;

/// <summary>
/// Climate tuning, the weather at the camera, and a map of the weather around it (Z down, so the view is from above):
/// cloud as grey, rain as blue, storm cores red, wind arrows, each pressure system's extent, the camera in white.
/// No GC allocation per frame.
/// </summary>
public sealed class WeatherPanel
{
    private const int TimingSamples = 256;
    private const float CellPixels = 8f;
    private const int ArrowEvery = 4;
    private const float ArrowPixelsPerMetrePerSecond = 0.8f;

    private static readonly float2 GraphSize = new(0f, 48f);

    private readonly WeatherMap _map;
    private WeatherClimate _edit;
    private int _seed;

    public readonly TimingRing TickMs = new(TimingSamples);
    public bool Open = true;
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

        ImGui.SeparatorText("Map (32 km)"u8);
        var corner = ImGui.GetCursorScreenPos();
        var list = ImGui.GetWindowDrawList();
        ImGui.Dummy(new float2(side, side));
        list.PushClipRect(corner, corner + new float2(side, side), true);

        for (var j = 0; j < WeatherMap.Size; j++)
        {
            for (var i = 0; i < WeatherMap.Size; i++)
            {
                var s = _map.Sample(origin.X + (i + 0.5f) * WeatherMap.Spacing, origin.Y + (j + 0.5f) * WeatherMap.Spacing);
                var min = corner + new float2(i * CellPixels, j * CellPixels);
                list.AddRectFilled(min, min + new float2(CellPixels, CellPixels), Shade(s));
            }
        }

        for (var j = ArrowEvery / 2; j < WeatherMap.Size; j += ArrowEvery)
        {
            for (var i = ArrowEvery / 2; i < WeatherMap.Size; i += ArrowEvery)
            {
                var wind = _map.Sample(origin.X + i * WeatherMap.Spacing, origin.Y + j * WeatherMap.Spacing).Wind;
                var tail = corner + new float2(i * CellPixels, j * CellPixels);
                var head = tail + new float2(wind.X, wind.Y) * ArrowPixelsPerMetrePerSecond;
                list.AddLine(tail, head, new ImColor8(255, 255, 255, 200), 1.5f);
                list.AddCircleFilled(head, 2f, new ImColor8(255, 255, 255, 200), 6);
            }
        }

        foreach (ref readonly var system in _map.Systems)
        {
            var centre = corner + Pixels(system.Centre - origin, pixelsPerMetre);
            list.AddCircle(centre, system.Radius * pixelsPerMetre, KindColour(system.Kind, system.Strength), 32, 1.5f);
        }

        list.AddCircleFilled(corner + Pixels(camera - origin, pixelsPerMetre), 3.5f, new ImColor8(255, 255, 255, 255), 12);
        list.PopClipRect();
    }

    private void DrawPerf()
    {
        ImGui.SeparatorText("Perf"u8);
        ImGui.Text($"Tick     {TickMs.Average,7:F4} ms  max {TickMs.Max,7:F4}");
        ImGui.Text($"Rebuild  {_map.LastRebuildMs,7:F3} ms  ({_map.Rebuilds} rebuilds)");
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
