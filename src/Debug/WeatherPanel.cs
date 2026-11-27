using System.Numerics;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using BrutalGame.DesignKit.Ui;
using BrutalGame.Engine.Time;
using BrutalGame.Water.Ocean;
using BrutalGame.Weather;

namespace BrutalGame.Debug;

/// <summary>
/// The sphere weather where the camera is: weather time (an offset from sim time, and a jump to the next spell of
/// strong wind here), the local wind, pressure and sky, the sea tile's wind key and swell, the pressure systems in
/// reach as a map (north up, wheel zooms, hover for a system, sea tile edges drawn) and a table, and the cache's and
/// the sea's tick times. No GC allocation per frame.
/// </summary>
public sealed class WeatherPanel : DebugPanel
{
    private const int TimingSamples = 256;
    private const int MaxSystems = 256;
    private const int Listed = 12;
    private const double MinReach = 20_000.0;
    private const double MaxReach = 2_000_000.0;
    private const double ZoomStep = 0.85;
    private const float ArrowPixelsPerMetrePerSecond = 3f;
    private const int TileGrid = 48;
    private const double TileGridMove = 500.0;
    private const double FindStepMinutes = 5.0;
    private const double FindDays = 14.0;

    private static readonly ImColor8 MapBack = new(20, 24, 31, 255);
    private static readonly ImColor8 TileEdge = new(255, 255, 255, 40);
    private static readonly ImColor8 White = new(255, 255, 255, 230);

    private readonly SphereWeatherCache _cache;
    private readonly SeaSwells _sea;
    private readonly SphereSystem[] _systems = new SphereSystem[MaxSystems];
    private readonly float[] _distance = new float[MaxSystems];
    private readonly int[] _order = new int[MaxSystems];
    private readonly int[] _tiles = new int[TileGrid * TileGrid];
    private double3 _tilesAt;
    private double _tilesReach;
    private double _reach = 150_000.0;
    private bool _mapHovered;
    private float _findWind = 12f;
    private string _findResult = "";

    public readonly TimingRing TickMs = new(TimingSamples);
    public readonly TimingRing SeaMs = new(TimingSamples);
    public bool DriveOcean = true;
    public bool DriveSky = true;

    /// <summary>Debug offset of weather time from sim time, to scrub through systems' lives.</summary>
    public float TimeOffsetMinutes;

    public WeatherPanel(SphereWeatherCache cache, SeaSwells sea) : base("Weather", GlfwKey.F6, "F6")
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

        if (ImGui.Begin(Title, ref Open, _mapHovered ? ImGuiWindowFlags.NoScrollWithMouse : ImGuiWindowFlags.None))
        {
            var unit = double3.Normalize(camera);
            DrawTime(unit, weatherTick);

            if (DebugUi.Section("At camera"u8))
                DrawLocal(unit, weatherTick);

            if (DebugUi.Section("Sea here"u8))
                DrawSea(camera);

            var count = Gather(unit, weatherTick);

            _mapHovered = false;

            if (DebugUi.Section("Map"u8))
                DrawMap(unit, weatherTick, count);

            if (DebugUi.Section("Systems"u8))
                DrawSystems(unit, count);

            if (DebugUi.Section("Perf"u8) && DebugUi.Timings("##weather-perf"u8))
            {
                DebugUi.Timing("Weather cache tick"u8, TickMs);
                DebugUi.Timing("Sea tick"u8, SeaMs);
                DebugUi.End();
            }
        }

        ImGui.End();
    }

    private void DrawTime(in double3 unit, long tick)
    {
        var minutes = tick * _cache.Field.TickSeconds / 60.0;
        var day = (int)(minutes / 1440.0);
        var inDay = minutes - day * 1440.0;
        ImGui.Text($"Weather day {day}  {(int)(inDay / 60.0):D2}:{(int)(inDay % 60.0):D2}");
        DebugUi.Help("Weather time: sim time plus the offset. It is a pure function of seed and tick, so every peer sees the same weather."u8);

        DesignerStyle.ItemWidth(150f);
        ImGui.InputFloat("Offset min"u8, ref TimeOffsetMinutes, 10f, 60f, "%.0f"u8);
        TimeOffsetMinutes = MathF.Max(0f, TimeOffsetMinutes);
        DebugUi.Tip("Minutes of weather time ahead of sim time; +/- step 10, Ctrl 60"u8);

        if (ImGui.Button("Next wind"u8))
            FindWind(unit, tick);

        DebugUi.Tip($"Jumps the offset to the next time the wind here reaches the speed on the right (searches {FindDays:F0} days in {FindStepMinutes:F0} min steps)");
        ImGui.SameLine();
        DesignerStyle.ItemWidth(110f);
        ImGui.SliderFloat("##findwind"u8, ref _findWind, 4f, 30f, "%.0f m/s"u8);

        if (_findResult.Length > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(_findResult);
        }

        ImGui.Checkbox("Drive ocean wind"u8, ref DriveOcean);
        ImGui.SameLine();
        ImGui.Checkbox("Drive sky"u8, ref DriveSky);
    }

    /// <summary>On a click only: field samples forward from now (about 4,000 at 3.5 µs).</summary>
    private void FindWind(in double3 unit, long tick)
    {
        var field = _cache.Field;
        var step = (long)Math.Round(FindStepMinutes * 60.0 / field.TickSeconds);
        var steps = (int)(FindDays * 1440.0 / FindStepMinutes);

        for (var i = 1; i <= steps; i++)
        {
            var t = tick + i * step;

            if (field.Sample(unit, t).WindSpeed < _findWind)
                continue;

            TimeOffsetMinutes += (float)(i * FindStepMinutes);
            _findResult = $"+{i * FindStepMinutes / 60.0:F1} h";
            return;
        }

        _findResult = "none in 14 days";
    }

    private void DrawLocal(in double3 unit, long tick)
    {
        var field = _cache.Field;
        var s = _cache.Sample(unit, tick);
        var latitude = Math.Asin(Math.Clamp(double3.Dot(unit, field.Pole), -1.0, 1.0)) * 180.0 / Math.PI;

        if (!DebugUi.Props("##local"u8))
            return;

        DebugUi.Row("Wind"u8, DebugUi.Grade(s.WindSpeed, 10f, 17f), $"{s.WindSpeed:F1} m/s toward {Bearing(s.Wind):F0} deg");
        DebugUi.Row("Gusts"u8, $"x{1f + s.Gust:F2}");
        DebugUi.Row("Pressure"u8, $"{s.Pressure:F1} hPa");
        DebugUi.Row("Sky"u8, $"cloud {100f * s.Cloud:F0}%  rain {100f * s.Rain:F0}%  storm {100f * s.Storm:F0}%");
        DebugUi.Row("Latitude"u8, $"{latitude:F1} deg");
        DebugUi.End();
    }

    private void DrawSea(in double3 camera)
    {
        var tile = _sea.TileAt(camera);

        if (!_sea.Driven)
        {
            DebugUi.Note("Pinned: one swell on every tile"u8);
            return;
        }

        if (_sea.For(tile) is not { } swell)
        {
            DebugUi.Text(DebugUi.Warn, $"tile {tile}: no live swell");
            return;
        }

        var wind = swell.Wind;

        if (!DebugUi.Props("##sea"u8))
            return;

        DebugUi.Row("Tile"u8, $"{tile}");
        DebugUi.Row("Wind key"u8, $"{wind.WindSpeed:F0} m/s toward {90f + wind.WindDirection * 180f / MathF.PI:F0} deg");
        DebugUi.Tip("The tile's 5 x 5 mean wind for this 60 s epoch, in 1 m/s and 10 deg steps"u8);
        DebugUi.Row("Hs / Tp"u8, $"{swell.SignificantWaveHeight:F2} m  {swell.PeakPeriod:F1} s");
        DebugUi.Row("Blend"u8, swell.BlendProgress < 1f ? DebugUi.Warn : DesignerStyle.Text, $"{100f * swell.BlendProgress:F0}%");
        DebugUi.Row("Live tiles"u8, _sea.Misses > 0 ? DebugUi.Bad : DesignerStyle.Text, $"{_sea.LiveCount}  {_sea.Misses} misses last tick");
        DebugUi.End();
    }

    private int Gather(in double3 unit, long tick)
    {
        var field = _cache.Field;
        var count = field.Gather(unit, _reach / field.Climate.Radius, tick * field.TickSeconds, _systems);

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
        ImGui.Text($"{count} within {_reach / 1000.0:F0} km; the {Math.Min(count, Listed)} nearest:");

        if (!ImGui.BeginTable("##systems"u8, 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.BordersInnerV))
            return;

        ImGui.TableSetupColumn("Kind"u8);
        ImGui.TableSetupColumn("km"u8);
        ImGui.TableSetupColumn("Bearing"u8);
        ImGui.TableSetupColumn("Radius"u8);
        ImGui.TableSetupColumn("Life"u8);
        ImGui.TableSetupColumn("Peak"u8, ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();
        _cache.Field.Basis(unit, out var east, out var north);

        for (var i = 0; i < Math.Min(count, Listed); i++)
        {
            ref readonly var s = ref _systems[_order[i]];
            var to = s.Centre - unit;
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DebugUi.Text(KindColour(s.Kind, 1f).AsFloat4(), Name(s.Kind));
            ImGui.TableNextColumn();
            ImGui.Text($"{_distance[_order[i]] / 1000f:F0}");
            ImGui.TableNextColumn();
            ImGui.Text($"{Bearing(new Vector2((float)double3.Dot(to, east), (float)double3.Dot(to, north))):F0} deg");
            ImGui.TableNextColumn();
            ImGui.Text($"{s.Radius / 1000f:F0} km");
            ImGui.TableNextColumn();
            ImGui.Text($"{100f * s.Strength:F0}%");
            DebugUi.Tip("Strength over the system's life: it grows, peaks and fades"u8);
            ImGui.TableNextColumn();
            ImGui.Text($"{s.PeakWind * s.Strength:F0} m/s  {s.Pressure * s.Strength:+0.0;-0.0} hPa");
        }

        ImGui.EndTable();
    }

    /// <summary>The reach round the camera, north up: sea tile edges, each system's extent by kind (strength as opacity), the wind at the camera.</summary>
    private void DrawMap(in double3 unit, long tick, int count)
    {
        var side = MathF.Min(ImGui.GetContentRegionAvail().X, DebugUi.Ems(20f));
        var corner = ImGui.GetCursorScreenPos();
        var size = new float2(side, side);
        var centre = corner + size * 0.5f;
        var scale = side * 0.5f / (float)_reach;
        var radius = _cache.Field.Climate.Radius;
        ImGui.Dummy(size);
        _mapHovered = ImGui.IsItemHovered();
        Zoom();

        var list = ImGui.GetWindowDrawList();
        list.PushClipRect(corner, corner + size, true);
        list.AddRectFilled(corner, corner + size, MapBack);
        _cache.Field.Basis(unit, out var east, out var north);
        DrawTiles(list, unit, east, north, corner, side, radius);

        var mouse = ImGui.GetMousePos();
        var hit = -1;
        var hitDistance = float.MaxValue;

        for (var i = 0; i < count; i++)
        {
            ref readonly var s = ref _systems[i];
            var to = (s.Centre - unit) * radius;
            var at = centre + new float2((float)double3.Dot(to, east) * scale, -(float)double3.Dot(to, north) * scale);
            list.AddCircle(at, s.Radius * scale, KindColour(s.Kind, s.Strength), 32, 1.5f);

            var d = MathF.Sqrt((mouse.X - at.X) * (mouse.X - at.X) + (mouse.Y - at.Y) * (mouse.Y - at.Y));

            if (_mapHovered && d < s.Radius * scale && d < hitDistance)
                (hit, hitDistance) = (i, d);
        }

        var wind = _cache.Sample(unit, tick).Wind * ArrowPixelsPerMetrePerSecond;
        list.AddLine(centre, centre + new float2(wind.X, -wind.Y), White, 1.5f);
        list.AddCircleFilled(centre, 3.5f, White, 12);
        list.AddText(corner + new float2(DebugUi.Ems(0.3f), DebugUi.Ems(0.2f)), White, $"{2.0 * _reach / 1000.0:F0} km across, N up");
        list.PopClipRect();

        if (hit >= 0)
        {
            ref readonly var s = ref _systems[hit];
            DebugUi.Tooltip($"{Name(s.Kind)}  {_distance[hit] / 1000f:F0} km away\nradius {s.Radius / 1000f:F0} km  life {100f * s.Strength:F0}%\npeak wind {s.PeakWind * s.Strength:F0} m/s  {s.Pressure * s.Strength:+0.0;-0.0} hPa\ncloud {100f * s.Cloud:F0}%  rain {100f * s.Rain:F0}%");
        }
        else if (_mapHovered)
            DebugUi.Tooltip("Wheel zooms; hover a system for its details"u8);
    }

    private void Zoom()
    {
        var wheel = ImGui.GetIO().MouseWheel;

        if (!_mapHovered || wheel == 0f)
            return;

        _reach = Math.Clamp(_reach * Math.Pow(ZoomStep, wheel), MinReach, MaxReach);
    }

    /// <summary>
    /// Sea tile edges: the tile under each point of a <see cref="TileGrid"/> square lattice over the map, an edge drawn
    /// where neighbours differ. The lattice is found again only when the camera moves <see cref="TileGridMove"/> or the map zooms.
    /// </summary>
    private void DrawTiles(ImDrawListPtr list, in double3 unit, in double3 east, in double3 north, float2 corner, float side, double radius)
    {
        var cell = side / TileGrid;

        if (double3.Distance(unit, _tilesAt) * radius > TileGridMove || _tilesReach != _reach)
        {
            (_tilesAt, _tilesReach) = (unit, _reach);
            var metres = 2.0 * _reach / TileGrid;

            for (var y = 0; y < TileGrid; y++)
            {
                for (var x = 0; x < TileGrid; x++)
                {
                    var e = ((x + 0.5) * metres - _reach) / radius;
                    var n = (_reach - (y + 0.5) * metres) / radius;
                    _tiles[y * TileGrid + x] = _sea.TileAt(unit + east * e + north * n);
                }
            }
        }

        for (var y = 0; y < TileGrid; y++)
        {
            for (var x = 0; x < TileGrid; x++)
            {
                var tile = _tiles[y * TileGrid + x];
                var at = corner + new float2(x * cell, y * cell);

                if (x + 1 < TileGrid && _tiles[y * TileGrid + x + 1] != tile)
                    list.AddLine(at + new float2(cell, 0f), at + new float2(cell, cell), TileEdge);

                if (y + 1 < TileGrid && _tiles[(y + 1) * TileGrid + x] != tile)
                    list.AddLine(at + new float2(0f, cell), at + new float2(cell, cell), TileEdge);
            }
        }
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
