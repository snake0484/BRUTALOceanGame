using System.Numerics;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.Engine.Time;
using BrutalGame.Water.Ocean;
using BrutalGame.Weather;

namespace BrutalGame.Debug;

/// <summary>
/// The sphere weather where the camera is: the local wind, pressure and sky, the sea tile's wind key and swell, the
/// pressure systems within <see cref="Reach"/> (listed nearest first and drawn on a map, north up), and the timings of the
/// weather cache's and the sea's ticks. No GC allocation per frame.
/// </summary>
public sealed class WeatherPanel
{
    private const int TimingSamples = 256;
    private const int MaxSystems = 256;
    private const int Listed = 10;
    private const double Reach = 150_000.0;
    private const float MapPixels = 280f;
    private const float ArrowPixelsPerMetrePerSecond = 3f;

    private static readonly float2 GraphSize = new(0f, 48f);

    private readonly SphereWeatherCache _cache;
    private readonly SeaSwells _sea;
    private readonly SphereSystem[] _systems = new SphereSystem[MaxSystems];
    private readonly float[] _distance = new float[MaxSystems];
    private readonly int[] _order = new int[MaxSystems];

    public readonly TimingRing TickMs = new(TimingSamples);
    public readonly TimingRing SeaMs = new(TimingSamples);
    public bool Open = true;
    public bool DriveOcean = true;
    public bool DriveSky = true;

    /// <summary>Debug offset of weather time from sim time, to scrub through systems' lives.</summary>
    public float TimeOffsetMinutes;

    public WeatherPanel(SphereWeatherCache cache, SeaSwells sea)
    {
        _cache = cache;
        _sea = sea;
    }

    public double TimeOffset => TimeOffsetMinutes * 60.0;

    /// <param name="camera">Planet frame.</param>
    public void Draw(in double3 camera, long weatherTick)
    {
        if (!Open)
            return;

        ImGui.SetNextWindowPos(new float2(796f, 12f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new float2(320f, 0f), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Weather"u8, ref Open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            var unit = double3.Normalize(camera);
            DrawControls();
            DrawLocal(unit, weatherTick);
            DrawSea(camera);
            var count = Gather(unit, weatherTick);
            DrawSystems(unit, count);
            DrawMap(unit, weatherTick, count);
            DrawPerf();
        }

        ImGui.End();
    }

    private void DrawControls()
    {
        ImGui.SliderFloat("Time offset min"u8, ref TimeOffsetMinutes, 0f, 600f, "%.0f"u8);
        ImGui.Checkbox("Drive ocean wind"u8, ref DriveOcean);
        ImGui.SameLine();
        ImGui.Checkbox("Drive sky"u8, ref DriveSky);
    }

    private void DrawLocal(in double3 unit, long tick)
    {
        var field = _cache.Field;
        var s = _cache.Sample(unit, tick);
        var latitude = Math.Asin(Math.Clamp(double3.Dot(unit, field.Pole), -1.0, 1.0)) * 180.0 / Math.PI;

        ImGui.SeparatorText("At camera"u8);
        ImGui.Text($"wind {s.WindSpeed:F1} m/s toward {Bearing(s.Wind):F0} deg  gusts x{1f + s.Gust:F2}");
        ImGui.Text($"{s.Pressure:F1} hPa  cloud {100f * s.Cloud:F0}%  rain {100f * s.Rain:F0}%  storm {100f * s.Storm:F0}%");
        ImGui.Text($"latitude {latitude:F1} deg  weather t {tick * field.TickSeconds / 60.0:F1} min");
    }

    private void DrawSea(in double3 camera)
    {
        var tile = _sea.TileAt(camera);

        ImGui.SeparatorText("Sea here"u8);

        if (!_sea.Driven)
        {
            ImGui.Text("pinned: one swell on every tile"u8);
            return;
        }

        if (_sea.For(tile) is not { } swell)
        {
            ImGui.Text($"tile {tile}: no live swell");
            return;
        }

        var wind = swell.Wind;
        ImGui.Text($"tile {tile}: wind {wind.WindSpeed:F0} m/s toward {90f + wind.WindDirection * 180f / MathF.PI:F0} deg (compass)");
        ImGui.Text($"Hs {swell.SignificantWaveHeight:F2} m  Tp {swell.PeakPeriod:F1} s  blend {100f * swell.BlendProgress:F0}%");
        ImGui.Text($"{_sea.LiveCount} tiles live  {_sea.Misses} misses last tick");
    }

    private int Gather(in double3 unit, long tick)
    {
        var field = _cache.Field;
        var count = field.Gather(unit, Reach / field.Climate.Radius, tick * field.TickSeconds, _systems);

        for (var i = 0; i < count; i++)
        {
            _distance[i] = (float)(Math.Acos(Math.Clamp(double3.Dot(unit, _systems[i].Centre), -1.0, 1.0)) * field.Climate.Radius);
            _order[i] = i;
        }

        var listed = Math.Min(count, Listed);

        for (var i = 0; i < listed; i++)
        {
            var best = i;

            for (var j = i + 1; j < count; j++)
            {
                if (_distance[_order[j]] < _distance[_order[best]])
                    best = j;
            }

            (_order[i], _order[best]) = (_order[best], _order[i]);
        }

        return count;
    }

    private void DrawSystems(in double3 unit, int count)
    {
        ImGui.SeparatorText("Systems within 150 km"u8);
        ImGui.Text($"{count} reach; nearest:");
        _cache.Field.Basis(unit, out var east, out var north);

        for (var i = 0; i < Math.Min(count, Listed); i++)
        {
            ref readonly var s = ref _systems[_order[i]];
            var to = s.Centre - unit;
            var bearing = Bearing(new Vector2((float)double3.Dot(to, east), (float)double3.Dot(to, north)));
            ImGui.Text($"{Name(s.Kind),-5} {_distance[_order[i]] / 1000f,5:F0} km {bearing,4:F0} deg  r {s.Radius / 1000f:F0} km  {100f * s.Strength,3:F0}%  {s.PeakWind * s.Strength:F0} m/s  {s.Pressure * s.Strength:F1} hPa");
        }
    }

    /// <summary>150 km round the camera, north up: each system's extent by kind, its strength as opacity, the wind at the camera.</summary>
    private void DrawMap(in double3 unit, long tick, int count)
    {
        var corner = ImGui.GetCursorScreenPos();
        var list = ImGui.GetWindowDrawList();
        var centre = corner + new float2(MapPixels * 0.5f, MapPixels * 0.5f);
        var scale = MapPixels * 0.5f / (float)Reach;
        var radius = _cache.Field.Climate.Radius;
        ImGui.Dummy(new float2(MapPixels, MapPixels));
        list.PushClipRect(corner, corner + new float2(MapPixels, MapPixels), true);
        list.AddRectFilled(corner, corner + new float2(MapPixels, MapPixels), new ImColor8(28, 32, 40, 255));
        _cache.Field.Basis(unit, out var east, out var north);

        for (var i = 0; i < count; i++)
        {
            ref readonly var s = ref _systems[i];
            var to = (s.Centre - unit) * radius;
            var at = centre + new float2((float)double3.Dot(to, east) * scale, -(float)double3.Dot(to, north) * scale);
            list.AddCircle(at, s.Radius * scale, KindColour(s.Kind, s.Strength), 32, 1.5f);
        }

        var wind = _cache.Sample(unit, tick).Wind * ArrowPixelsPerMetrePerSecond;
        list.AddLine(centre, centre + new float2(wind.X, -wind.Y), new ImColor8(255, 255, 255, 220), 1.5f);
        list.AddCircleFilled(centre, 3.5f, new ImColor8(255, 255, 255, 255), 12);
        list.PopClipRect();
    }

    private void DrawPerf()
    {
        ImGui.SeparatorText("Perf"u8);
        ImGui.Text($"cache tick {TickMs.Average,7:F4} ms  max {TickMs.Max,7:F4}  ({_cache.ReadyEntries} snapshots, {_cache.DroppedTiles} dropped)");
        ImGui.Text($"sea tick   {SeaMs.Average,7:F4} ms  max {SeaMs.Max,7:F4}  ({_sea.LastKeys} winds read, {_sea.LastMade} tiles made)");
        ImGui.PlotLines("##weather"u8, TickMs.Values, TickMs.Offset, default, 0f, MathF.Max(TickMs.Max, 0.01f), GraphSize);
        ImGui.PlotLines("##sea"u8, SeaMs.Values, SeaMs.Offset, default, 0f, MathF.Max(SeaMs.Max, 0.01f), GraphSize);
    }

    /// <summary>Compass bearing (deg, clockwise from north) of an (east, north) vector.</summary>
    private static float Bearing(Vector2 v)
    {
        var deg = MathF.Atan2(v.X, v.Y) * 180f / MathF.PI;
        return deg < 0f ? deg + 360f : deg;
    }

    private static string Name(PressureKind kind) => kind switch
    {
        PressureKind.Storm => "storm",
        PressureKind.Low => "low",
        _ => "high",
    };

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
