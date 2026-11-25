using System.Globalization;
using System.Numerics;
using System.Text;
using Brutal.Logging;
using Brutal.Numerics;
using BrutalGame.Engine.Scene;
using BrutalGame.Frames;

namespace BrutalGame;

/// <summary>Which of the test planet's frames the --orbit-test camera flies in.</summary>
public enum OrbitTestFrame
{
    /// <summary>The rotating surface frame: the planet holds still under the camera.</summary>
    Surface,

    /// <summary>The planet's inertial frame (--orbit-test inertial): the planet turns under the camera at render rate.</summary>
    Inertial,
}

/// <summary>
/// --orbit-test, the M1 precision exit check: once the world has loaded, flies the camera from 2 m above the test planet
/// (drawn as a smooth sphere, lit by the star through the frame tree) out to 1e8 m along <see cref="OrbitPath"/>. The
/// camera moves by per-frame deltas in double, as a flown camera would. Each frame records altitude, camera error
/// against the closed-form path, the marker's float-pipeline pixel against the exact projection (jitter = change in that
/// error between frames, so the expected motion is removed), the drawn marker against its closed-form position at the
/// frame's render time (pose error: the planet's render-rate pose, X5), depth and impostor surface precision, and frame
/// timings. The marker is fixed to the surface; in the inertial variant it sits 1e-3 rad off the spin pole so it turns
/// under the camera slowly enough to stay in view. Logs a summary per altitude decade, PASS or FAIL, writes every frame
/// to orbit_test.csv, and closes the window.
/// </summary>
public sealed class OrbitTest
{
    public const float JitterLimit = 0.1f;
    public const float PoseErrorLimit = 0.1f;

    private const double CameraErrorLimit = 1e-3;
    private const float SurfaceErrorLimit = 1e-4f;
    private const double Hold = 1.0;
    private const double Seconds = 30.0;
    private const double MarkerPixels = 8.0;
    private const double PoleOffset = 1e-3;
    private const int MaxFrames = 1 << 16;
    private const int Bands = 8;
    private const string CsvName = "orbit_test.csv";

    private static readonly LogCategory Log = LogCategory.Make("OrbitTest");

    private readonly FlyCamera _camera;
    private readonly FrameTree _frames;
    private readonly FrameId _planet;
    private readonly FrameId _star;
    private readonly OrbitPath _path;
    private readonly double3 _spinAxis;
    private readonly double _daySeconds;

    private readonly double[] _time = new double[MaxFrames];
    private readonly double[] _altitude = new double[MaxFrames];
    private readonly double[] _cameraError = new double[MaxFrames];
    private readonly OrbitSample[] _samples = new OrbitSample[MaxFrames];
    private readonly float[] _jitter = new float[MaxFrames];
    private readonly float[] _poseError = new float[MaxFrames];
    private readonly float[] _tickPoseError = new float[MaxFrames];
    private readonly float[] _frameMs = new float[MaxFrames];
    private readonly float[] _workMs = new float[MaxFrames];
    private readonly float[] _gpuMs = new float[MaxFrames];
    private int _count;
    private double _start = -1.0;
    private double _s;
    private double3 _markerSurface;
    private double3 _markerStart;
    private float _framePoseError;
    private float _frameTickPoseError;

    /// <param name="camera">Its frame must be the planet's surface frame; the inertial variant moves it to the planet's inertial frame.</param>
    public OrbitTest(FlyCamera camera, CelestialSystem celestial, OrbitTestFrame frame)
    {
        _camera = camera;
        _frames = celestial.Frames;
        Frame = frame;

        for (var i = 0; i < celestial.Count; i++)
        {
            ref readonly var body = ref celestial[i];

            if (body.Surface != camera.Frame)
                continue;

            _planet = body.Surface;
            PlanetRadius = body.Radius;
            _star = celestial[Root(celestial, i)].Inertial;
            _spinAxis = body.SpinAxis;
            _daySeconds = body.DayTicks * celestial.TickSeconds;

            if (frame == OrbitTestFrame.Inertial)
                camera.Frame = body.Inertial;

            break;
        }

        if (PlanetRadius <= 0.0)
            throw new InvalidOperationException("The orbit test camera is not in a body's surface frame.");

        UpdatePoses();

        // Surface: the marker sits ~50 degrees from the subsolar point at the start, lit, with the terminator in view from
        // orbit. Inertial: near the spin pole, tipped toward the star, so it moves ~0.3 m/s (5 mm a tick) under the camera.
        var sun = new double3(SunDirection.X, SunDirection.Y, SunDirection.Z);
        var side = double3.Cross(sun, Math.Abs(sun.Y) < 0.9 ? double3.UnitY : double3.UnitX);
        var toward = double3.Normalize(sun - _spinAxis * double3.Dot(sun, _spinAxis));
        var markerUp = frame == OrbitTestFrame.Surface
            ? sun + double3.Normalize(side) * 1.2
            : _spinAxis * Math.Cos(PoleOffset) + toward * Math.Sin(PoleOffset);
        _path = new OrbitPath(PlanetRadius, markerUp);
        _markerSurface = _path.Marker;
    }

    public OrbitTestFrame Frame { get; }

    public bool Done { get; private set; }

    public double PlanetRadius { get; }

    /// <summary>Planet centre and axes in the camera's frame, at render time.</summary>
    public double3 PlanetCentre { get; private set; }

    public doubleQuat PlanetAxes { get; private set; }

    /// <summary>Toward the star, in the camera frame's axes.</summary>
    public Vector3 SunDirection { get; private set; }

    /// <summary>The surface marker in the camera's frame, at render time.</summary>
    public double3 Marker { get; private set; }

    /// <summary>About <see cref="MarkerPixels"/> pixels across on screen.</summary>
    public double MarkerRadius { get; private set; }

    /// <summary>
    /// Before the frame's work, after the render poses: moves the camera along the path by this frame's delta and checks the
    /// drawn marker against its closed form at <paramref name="renderSeconds"/>.
    /// </summary>
    public void Fly(double time, bool ready, float viewportHeight, double renderSeconds)
    {
        if (Done)
            return;

        if (_start < 0.0)
        {
            if (!ready)
                return;

            _start = time;
            _s = 0.0;
            _camera.Position = _path.Camera(0.0);
            UpdatePoses();
            _markerSurface = doubleQuat.Conjugate(PlanetAxes) * (_path.Marker - PlanetCentre);
            _markerStart = _path.Marker;
        }

        var s = Math.Clamp((time - _start - Hold) / Seconds, 0.0, 1.0);
        _camera.Position += _path.Camera(s) - _path.Camera(_s);
        _s = s;
        UpdatePoses();
        _camera.LookAlong(_path.LookAt(s, _camera.Position, Marker) - _camera.Position);

        var range = (Marker - _camera.Position).Length();
        var focal = viewportHeight / (2.0 * Math.Tan(_camera.FovY * 0.5));
        MarkerRadius = range * MarkerPixels * Math.Tan(_camera.FovY * 0.5) / viewportHeight;
        var closedForm = ClosedFormMarker(renderSeconds);
        _framePoseError = (float)((Marker - closedForm).Length() / range * focal);

        // What drawing from the tick's poses instead would show: the teeth of the pose error.
        _frames.TryRelative(_planet, _camera.Frame, out var tick);
        _frameTickPoseError = (float)((tick.TransformPoint(_markerSurface) - closedForm).Length() / range * focal);
    }

    /// <summary>After the frame: probes the pose it was drawn with.</summary>
    public void Record(double time, float aspect, Vector2 viewport, float frameMs, float workMs, float gpuMs)
    {
        if (Done || _start < 0.0)
            return;

        if (time - _start > Hold + Seconds || _count == MaxFrames)
        {
            Report(viewport);
            Done = true;
            return;
        }

        var i = _count++;
        var sample = _path.Measure(Marker, _camera.Position, _camera.Rotation, _camera.ViewProjection(aspect), _camera.FovY, aspect, viewport);
        _samples[i] = sample;
        _jitter[i] = i > 0 ? Vector2.Distance(sample.Error, _samples[i - 1].Error) : 0f;
        _poseError[i] = _framePoseError;
        _tickPoseError[i] = _frameTickPoseError;
        _time[i] = time - _start;
        _altitude[i] = OrbitPath.Altitude(_s);
        _cameraError[i] = (_camera.Position - _path.Camera(_s)).Length();
        _frameMs[i] = frameMs;
        _workMs[i] = workMs;
        _gpuMs[i] = gpuMs;
    }

    private static int Root(CelestialSystem celestial, int index)
    {
        while (celestial[index].Parent >= 0)
            index = celestial[index].Parent;

        return index;
    }

    /// <summary>The frame chain at render time (<see cref="FrameTree.TryRelativeRender"/>), as the draws use it.</summary>
    private void UpdatePoses()
    {
        _frames.TryRelativeRender(_planet, _camera.Frame, out var planet);
        _frames.TryRelativeRender(_star, _camera.Frame, out var star);
        PlanetCentre = planet.Position;
        PlanetAxes = planet.Rotation;
        Marker = planet.TransformPoint(_markerSurface);
        var sun = double3.Normalize(star.Position);
        SunDirection = new Vector3((float)sun.X, (float)sun.Y, (float)sun.Z);
    }

    /// <summary>The marker in the camera's frame from the spin's closed form in seconds, independent of the tick split.</summary>
    private double3 ClosedFormMarker(double renderSeconds)
    {
        if (Frame == OrbitTestFrame.Surface)
            return _markerSurface;

        var turns = renderSeconds / _daySeconds;
        return doubleQuat.CreateFromAxisAngle(_spinAxis, 2.0 * Math.PI * (turns - Math.Floor(turns))) * _markerSurface;
    }

    private static int Band(double altitude) => Math.Clamp((int)Math.Floor(Math.Log10(altitude)), 0, Bands - 1);

    private void Report(Vector2 viewport)
    {
        var pass = _count > 0;
        var worstJitter = 0f;
        var worstPose = 0f;
        var worstTickPose = 0f;
        Log.Info($"orbit test ({Frame} frame): {_count} frames over {Hold + Seconds:F0} s, {OrbitPath.StartAltitude} m to {OrbitPath.EndAltitude:E0} m over a {PlanetRadius / 1000.0:F0} km sphere, viewport {viewport.X}x{viewport.Y}");
        Log.Info("altitude band   frames  jitter max px  pose err max px  pixel err max px  camera err max m  surface err max  depth  finite  frame p50/max ms  work p50 ms  gpu planet p50 ms");

        for (var i = 0; i < _count; i++)
            worstTickPose = MathF.Max(worstTickPose, _tickPoseError[i]);

        for (var band = 0; band < Bands; band++)
        {
            var frames = 0;
            var jitter = 0f;
            var pose = 0f;
            var pixel = 0f;
            var cameraError = 0.0;
            var surface = 0f;
            var depthOk = true;
            var finite = true;
            var frame = new List<float>();
            var work = new List<float>();
            var gpu = new List<float>();

            for (var i = 0; i < _count; i++)
            {
                if (Band(_altitude[i]) != band)
                    continue;

                ref readonly var sample = ref _samples[i];
                frames++;
                jitter = MathF.Max(jitter, _jitter[i]);
                pose = MathF.Max(pose, _poseError[i]);
                pixel = MathF.Max(pixel, sample.Error.Length());
                cameraError = Math.Max(cameraError, _cameraError[i]);
                surface = MathF.Max(surface, sample.SurfaceError);
                depthOk &= sample.DepthOk;
                finite &= sample.Finite && float.IsFinite(_jitter[i]) && float.IsFinite(_poseError[i]) && double.IsFinite(_cameraError[i]);
                frame.Add(_frameMs[i]);
                work.Add(_workMs[i]);
                gpu.Add(_gpuMs[i]);
            }

            if (frames == 0)
            {
                Log.Info($"{Math.Pow(10, band),8:G2}-{Math.Pow(10, band + 1):G2} m  no frames");
                pass = false;
                continue;
            }

            frame.Sort();
            work.Sort();
            gpu.Sort();
            worstJitter = MathF.Max(worstJitter, jitter);
            worstPose = MathF.Max(worstPose, pose);
            pass &= jitter < JitterLimit && pose < PoseErrorLimit && depthOk && finite && cameraError < CameraErrorLimit && surface < SurfaceErrorLimit;
            Log.Info($"{Math.Max(OrbitPath.StartAltitude, Math.Pow(10, band)),8:G2}-{Math.Pow(10, band + 1):G2} m {frames,7} {jitter,14:E2} {pose,16:E2} {pixel,17:E2} {cameraError,17:E2} {surface,16:E2}  {(depthOk ? "ok" : "BAD"),5}  {(finite ? "ok" : "NaN"),6}  {frame[frame.Count / 2],7:F2}/{frame[^1]:F2} {work[work.Count / 2],12:F2} {gpu[gpu.Count / 2],18:F4}");
        }

        var csv = WriteCsv();
        Log.Info($"marker moved {(Marker - _markerStart).Length():F3} m under the camera; drawn from tick poses instead, max pose error would be {worstTickPose:E2} px");
        Log.Info($"orbit test {(pass ? "PASS" : "FAIL")}: max jitter {worstJitter:E2} px (limit {JitterLimit}), max pose error {worstPose:E2} px (limit {PoseErrorLimit}), depth reverse-Z infinite, NaN/Inf checked, camera error < {CameraErrorLimit} m, surface error < {SurfaceErrorLimit}; per frame: {csv}");
    }

    private string WriteCsv()
    {
        var builder = new StringBuilder("frame,time_s,altitude_m,camera_err_m,pixel_x,pixel_y,exact_x,exact_y,jitter_px,pose_err_px,tick_pose_err_px,depth,depth_ok,surface_err,frame_ms,work_ms,gpu_planet_ms\n");

        for (var i = 0; i < _count; i++)
        {
            ref readonly var s = ref _samples[i];
            builder.Append(CultureInfo.InvariantCulture,
                $"{i},{_time[i]:F4},{_altitude[i]:G9},{_cameraError[i]:E3},{s.Pixel.X:F5},{s.Pixel.Y:F5},{s.Exact.X:F5},{s.Exact.Y:F5},{_jitter[i]:E3},{_poseError[i]:E3},{_tickPoseError[i]:E3},{s.Depth:E6},{(s.DepthOk ? 1 : 0)},{s.SurfaceError:E3},{_frameMs[i]:F3},{_workMs[i]:F3},{_gpuMs[i]:F4}\n");
        }

        var path = Path.GetFullPath(CsvName);
        File.WriteAllText(path, builder.ToString());
        return path;
    }
}
