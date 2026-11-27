using System.Numerics;
using Brutal.Concurrency.Jobs;
using Brutal.Numerics;
using Brutal.VulkanApi;
using BrutalGame.Engine.Jobs;
using BrutalGame.Engine.Rendering;
using BrutalGame.Engine.Scene;
using BrutalGame.Frames;
using BrutalGame.Sim;
using BrutalGame.Terrain;
using BrutalGame.Terrain.Runtime.Rendering;

namespace BrutalGame;

/// <summary>
/// Every celestial body as the client draws it past its octree (planets.md "Detail ladder", stages 1-3): points of light,
/// discs and baked cube-map impostors with the atmosphere, lit by the star through the frame tree's render poses, with
/// eclipses by the two bodies that look largest from each. Picks the focus body (the rocky body the camera is lowest
/// over, with hysteresis) whose octree streams, moves the free camera into its surface frame, and upgrades its impostor
/// to <see cref="PlanetImpostor.FocusSize"/>² on a worker. Each rocky body's 256² impostor bakes at load. Main thread
/// except the bakes.
/// </summary>
public sealed class CelestialView : IDisposable
{
    /// <summary>The star's light on the bodies (the ladder's exposure).</summary>
    public const float SunIntensity = 1.5f;

    /// <summary>A body becomes the focus once the camera is this share of the focus body's altitude over it.</summary>
    private const double FocusHysteresis = 0.8;

    private const double AtmosphereScaleHeight = 8000.0;
    private const double AtmosphereScaleHeights = 12.0;

    /// <summary>Bodies the camera is within this many bounding radii of draw a fullscreen triangle, not a quad.</summary>
    private const double FullscreenShare = 1.2;

    private const int RetireFrames = Renderer.FramesInFlight + 1;

    private static readonly Vector3 StarEmission = new(9f, 8.5f, 7.6f);

    private sealed class Body
    {
        public int Index;
        public CelestialKind Kind;
        public double Radius;
        public FrameId Surface;
        public PlanetGenerator? Generator;
        public int TerrainBody = -1;
        public ImpostorTexture? Texture;
        public VkDescriptorSet Set;

        /// <summary>Sea level, or 0 with no sea (m about the radius): the disc's radius.</summary>
        public double BaseHeight;

        /// <summary>Atmosphere top (m from the centre); 0 for none.</summary>
        public double AtmosphereTop;

        public double3 Centre;
        public doubleQuat Axes;
        public double Distance;
        public bool Posed;
    }

    private readonly record struct Retired(long Frame, ImpostorTexture Texture, VkDescriptorSet Set);

    /// <summary>Descriptor sets for the large impostor: one in use, the rest waiting out the frames in flight.</summary>
    private const int FocusSets = 3;

    private readonly VulkanContext _context;
    private readonly FrameTree _frames;
    private readonly BodyRenderer _renderer;
    private readonly GpuTimer _timer;
    private readonly JobScheduler _scheduler;
    private readonly Body[] _bodies;
    private readonly PlanetGenerator[] _generators;
    private readonly int _star = -1;
    private readonly ImpostorTexture _placeholder;
    private readonly VkDescriptorSet _placeholderSet;
    private readonly List<Retired> _retired = new(8);
    private readonly List<(long Frame, ImpostorTexture Texture)> _staging = new(8);
    private readonly Stack<VkDescriptorSet> _freeFocusSets = new(FocusSets);
    private ImpostorTexture? _focusTexture;
    private VkDescriptorSet _focusSet;
    private int _focusTextureBody = -1;
    private int _focus;
    private long _frame;

    public CelestialView(Renderer renderer, SimWorld sim)
    {
        _context = renderer.Context;
        _frames = sim.Frames;
        _timer = renderer.GpuTimer;
        _renderer = new BodyRenderer(renderer);
        _scheduler = new JobScheduler((uint)WorkerBudget.Side, "Impostor", ThreadPriority.BelowNormal);
        _placeholder = new ImpostorTexture(_context, null, 1);
        _placeholderSet = NewSet(_placeholder);

        for (var i = 0; i < FocusSets; i++)
            _freeFocusSets.Push(_renderer.AllocateSet());

        PointScope = _timer.Register("Body points");
        DiscScope = _timer.Register("Body discs");
        ImpostorScope = _timer.Register("Body impostors");

        var celestial = sim.Celestial;
        _bodies = new Body[Math.Min(celestial.Count, BodyRenderer.MaxBodies - 1)];
        var generators = new List<PlanetGenerator>();

        for (var i = 0; i < _bodies.Length; i++)
        {
            ref readonly var c = ref celestial[i];
            var body = new Body { Index = i, Kind = c.Kind, Radius = c.Radius, Surface = c.Surface };
            _bodies[i] = body;

            if (c.Kind == CelestialKind.Star)
            {
                _star = _star < 0 ? i : _star;
                continue;
            }

            if (sim.BodyParams[i] is not { } p)
                continue;

            body.Generator = i == sim.PlanetIndex ? sim.Generator : new PlanetGenerator(p);
            body.TerrainBody = generators.Count;
            body.BaseHeight = p.HasSea ? p.SeaHeight : 0.0;
            body.AtmosphereTop = c.Kind == CelestialKind.Planet ? p.Radius + body.BaseHeight + AtmosphereScaleHeights * AtmosphereScaleHeight : 0.0;
            body.Texture = new ImpostorTexture(_context, body.Generator, PlanetImpostor.BaseSize);
            body.Set = NewSet(body.Texture);
            body.Texture.QueueBake(_scheduler);
            generators.Add(body.Generator);
        }

        _scheduler.ExecuteJobs();
        _generators = generators.ToArray();
        _focus = sim.PlanetIndex;
    }

    public int PointScope { get; }

    public int DiscScope { get; }

    public int ImpostorScope { get; }

    /// <summary>Rocky bodies' generators in terrain-body order (TerrainWorld's list).</summary>
    public ReadOnlySpan<PlanetGenerator> TerrainGenerators => _generators;

    /// <summary>Keep the focus where it is and never move the camera (scripted tests in a fixed frame).</summary>
    public bool Pinned { get; set; }

    /// <summary>Celestial index of the focus body.</summary>
    public int Focus => _focus;

    public int FocusTerrainBody => _bodies[_focus].TerrainBody;

    public FrameId FocusSurface => _bodies[_focus].Surface;

    public PlanetGenerator FocusGenerator => _bodies[_focus].Generator!;

    /// <summary>The camera's height above the focus body's datum sphere (m), this frame.</summary>
    public double FocusAltitude { get; private set; }

    /// <summary>The focus body's atmosphere top (m from its centre); 0 for an airless body.</summary>
    public float FocusAtmosphereTop => (float)_bodies[_focus].AtmosphereTop;

    /// <summary>The radius the focus body's air density is 1 at: sea level, or the datum.</summary>
    public float FocusAtmosphereBase => (float)(_bodies[_focus].Radius + _bodies[_focus].BaseHeight);

    public float FocusScaleHeight => (float)AtmosphereScaleHeight;

    /// <summary>Every rocky body's base impostor is on the GPU.</summary>
    public bool Ready { get; private set; }

    /// <summary>The focus body's impostor is the <see cref="PlanetImpostor.FocusSize"/>² bake.</summary>
    public bool FocusUpgraded => _focusTextureBody == _focus && _focusTexture is { Uploaded: true };

    /// <summary>Wall time of the last focus bake (ms), 0 before one finishes.</summary>
    public float FocusBakeMs => _focusTexture is { Baked: true } t ? t.BakeMs : 0f;

    /// <summary>Wall time of the slowest base bake (ms).</summary>
    public float BaseBakeMs { get; private set; }

    /// <summary>Focus changes since start.</summary>
    public int FocusSwitches { get; private set; }

    /// <summary>The star's direction (unit, camera frame axes) from a body's centre; zero with no star.</summary>
    public double3 SunFrom(int index)
    {
        if (_star < 0 || !_bodies[_star].Posed || !_bodies[index].Posed)
            return double3.Zero;

        return double3.Normalize(_bodies[_star].Centre - _bodies[index].Centre);
    }

    /// <summary>
    /// Before the frame's terrain: every body's render pose in the camera's frame, then the focus. A new focus moves the
    /// camera into its surface frame (unless <see cref="Pinned"/>) and asks for its large impostor.
    /// </summary>
    /// <summary>Swaps a body's generator (the planet designer changed its authored layer); its large impostor rebakes. The 256² one is not rebaked.</summary>
    public void ReplaceGenerator(int index, PlanetGenerator generator)
    {
        var body = _bodies[index];
        body.Generator = generator;
        _generators[body.TerrainBody] = generator;

        if (_focusTextureBody == index)
            _focusTextureBody = -1;
    }

    public void Update(FlyCamera camera)
    {
        Pose(camera);

        if (!Pinned)
        {
            var best = _focus;

            foreach (var body in _bodies)
            {
                if (body.Generator is not null && body.Posed && Altitude(body) < Altitude(_bodies[best]))
                    best = body.Index;
            }

            if (best != _focus && Altitude(_bodies[best]) < FocusHysteresis * Altitude(_bodies[_focus]))
            {
                _focus = best;
                FocusSwitches++;
                MoveCamera(camera, _bodies[best].Surface);
                Pose(camera);
            }
        }

        FocusAltitude = Altitude(_bodies[_focus]);
    }

    /// <summary>Outside a render pass, once per frame: uploads finished bakes, frees what the GPU is done with, starts the focus bake.</summary>
    public void RecordUploads(CommandBuffer commands)
    {
        _frame++;
        Upload(commands, _placeholder);
        var ready = true;

        foreach (var body in _bodies)
        {
            if (body.Texture is null)
                continue;

            Upload(commands, body.Texture);
            ready &= body.Texture.Uploaded;
            BaseBakeMs = MathF.Max(BaseBakeMs, body.Texture.BakeMs);
        }

        Ready = ready;

        if (_focusTexture is not null)
            Upload(commands, _focusTexture);

        for (var i = _staging.Count - 1; i >= 0; i--)
        {
            if (_frame - _staging[i].Frame < RetireFrames)
                continue;

            _staging[i].Texture.ReleaseStaging();
            _staging.RemoveAt(i);
        }

        for (var i = _retired.Count - 1; i >= 0; i--)
        {
            if (_frame - _retired[i].Frame < RetireFrames)
                continue;

            _freeFocusSets.Push(_retired[i].Set);
            _retired[i].Texture.Dispose();
            _retired.RemoveAt(i);
        }

        if (_focusTextureBody != _focus && ready && !_scheduler.IsExecuting && _freeFocusSets.Count > 0)
            StartFocusBake();
    }

    /// <summary>
    /// Inside the opaque pass, after terrain: points of light, then discs, then impostors, each group far to near. The
    /// focus body's impostor is drawn against its octree as <paramref name="focus"/> says (<see cref="DetailLadder.Blend"/>).
    /// </summary>
    public void Draw(CommandBuffer commands, int frameIndex, VkExtent2D extent, FlyCamera camera, float aspect, FocusBlend focus)
    {
        var records = _renderer.Begin(frameIndex, camera, aspect, extent);
        var radiansPerPixel = 2.0 * Math.Tan(camera.FovY * 0.5) / extent.Height;
        Span<int> discs = stackalloc int[_bodies.Length + 1];
        Span<int> impostors = stackalloc int[_bodies.Length + 1];
        var discCount = 0;
        var impostorCount = 0;

        for (var i = 0; i < _bodies.Length; i++)
        {
            ref var record = ref records[i];
            record = default;
            var body = _bodies[i];

            if (!body.Posed)
                continue;

            var drawn = body.Kind == CelestialKind.Star ? FillStar(ref record, body, camera, radiansPerPixel) : FillRocky(ref record, body, camera, radiansPerPixel, focus);

            if (drawn == Stage.None)
                continue;

            var backdrop = i == _focus && focus.Backdrop > 0f;
            var group = drawn == Stage.Disc ? discs : impostors;
            ref var count = ref drawn == Stage.Disc ? ref discCount : ref impostorCount;

            // The backdrop is the focus body's record again as a disc (no relief to march: only gaps and the limb show it),
            // behind the octree, with the rim; it sorts just before the overlay.
            if (backdrop)
            {
                ref var behind = ref records[BackdropRecord];
                behind = record;
                behind.Shape.Z = focus.Backdrop * (1f - record.Colour.W);
                behind.Shape.W = DetailLadder.BackdropDepthScale;
                behind.Relief.Z = 0f;
                record.Lod.Z = 0f;
                group[count++] = BackdropRecord;
            }

            if (record.Shape.Z > 0f || record.Lod.Z > 0f)
                group[count++] = i;
        }

        SortFarToNear(discs[..discCount]);
        SortFarToNear(impostors[..impostorCount]);

        _timer.Begin(commands, PointScope);
        _renderer.DrawPoints(commands, frameIndex, extent, _bodies.Length);
        _timer.End(commands, PointScope);

        _timer.Begin(commands, DiscScope);
        DrawGroup(commands, frameIndex, extent, discs[..discCount]);
        _timer.End(commands, DiscScope);

        _timer.Begin(commands, ImpostorScope);
        DrawGroup(commands, frameIndex, extent, impostors[..impostorCount]);
        _timer.End(commands, ImpostorScope);
    }

    private enum Stage : byte
    {
        None,
        Disc,
        Impostor,
    }

    private void DrawGroup(CommandBuffer commands, int frameIndex, VkExtent2D extent, ReadOnlySpan<int> group)
    {
        if (group.IsEmpty)
            return;

        _renderer.BeginBodies(commands, frameIndex, extent);

        foreach (var i in group)
            _renderer.DrawBody(commands, i, SetOf(BodyOf(i)));
    }

    private int BackdropRecord => _bodies.Length;

    private Body BodyOf(int record) => _bodies[record == BackdropRecord ? _focus : record];

    private Stage FillStar(ref BodyDraw record, Body body, FlyCamera camera, double radiansPerPixel)
    {
        var relative = body.Centre - camera.Position;
        var diameter = DetailLadder.PixelDiameter(body.Radius, body.Distance, radiansPerPixel);
        var point = DetailLadder.PointWeight(diameter);
        record.Centre = new Vector4(ToFloat(relative), (float)body.Radius);
        record.Rotation = ToFloat(doubleQuat.Conjugate(body.Axes));
        record.Shape = new Vector4(1f, body.Distance < FullscreenShare * body.Radius ? 1f : 0f, 1f - point, 1f);
        record.Radii = new Vector4((float)body.Radius, 0f, 0f, 0f);
        record.Emission = new Vector4(StarEmission, 0f);
        record.Colour = new Vector4(DetailLadder.PointColour(StarEmission * 0.8f, diameter), point);
        return point < 1f && _placeholder.Uploaded ? Stage.Disc : Stage.None;
    }

    private Stage FillRocky(ref BodyDraw record, Body body, FlyCamera camera, double radiansPerPixel, FocusBlend focus)
    {
        var p = body.Generator!.Params;
        var relative = body.Centre - camera.Position;
        var toCamera = -relative / body.Distance;
        var bounding = Math.Max(body.Radius + p.MaxHeight, body.AtmosphereTop);
        var diameter = DetailLadder.PixelDiameter(body.Radius + body.BaseHeight, body.Distance, radiansPerPixel);
        var point = DetailLadder.PointWeight(diameter);
        var relief = DetailLadder.Relief(diameter);
        var blend = body.Index == _focus ? focus : FocusBlend.Alone;
        var ground = (1f - point) * blend.Overlay;
        var rim = (1f - point) * blend.Rim;
        var toBody = doubleQuat.Conjugate(body.Axes);
        var sun = SunFrom(body.Index);
        var texture = TextureOf(body);

        record.Centre = new Vector4(ToFloat(relative), (float)bounding);
        record.Rotation = ToFloat(toBody);
        record.Sun = new Vector4(ToFloat(toBody * sun), sun == double3.Zero ? 0f : SunIntensity);
        record.Shape = new Vector4(0f, body.Distance < FullscreenShare * bounding ? 1f : 0f, ground, blend.OverlayDepthScale);
        record.Radii = new Vector4((float)body.Radius, (float)body.BaseHeight, (float)body.AtmosphereTop, (float)AtmosphereScaleHeight);
        record.Relief = new Vector4(p.MinHeight, p.MaxHeight - p.MinHeight, relief, texture?.MaxSlope ?? 1f);
        record.Lod = new Vector4((float)PlanetImpostor.TexelAngle(texture?.Size ?? 1), (texture?.Levels ?? 1) - 1, rim, 0f);

        var phase = DetailLadder.LambertPhase((float)double3.Dot(sun, toCamera));
        var radiance = (texture?.MeanAlbedo ?? new Vector3(0.3f)) * (SunIntensity * (2f / 3f) * phase);
        record.Colour = new Vector4(DetailLadder.PointColour(radiance, diameter), point);
        Occluders(ref record, body, toBody);

        if (texture is null || (ground <= 0f && blend.Backdrop <= 0f && (rim <= 0f || body.AtmosphereTop <= 0.0)))
            return Stage.None;

        return relief > 0f ? Stage.Impostor : Stage.Disc;
    }

    /// <summary>The two rocky bodies that look largest from this one shadow it; the star's size sets the penumbra.</summary>
    private void Occluders(ref BodyDraw record, Body body, doubleQuat toBody)
    {
        var first = -1;
        var second = -1;
        var firstSize = 0.0;
        var secondSize = 0.0;

        foreach (var other in _bodies)
        {
            if (other == body || other.Generator is null || !other.Posed)
                continue;

            var size = other.Radius / (other.Centre - body.Centre).Length();

            if (size > firstSize)
            {
                (second, secondSize) = (first, firstSize);
                (first, firstSize) = (other.Index, size);
            }
            else if (size > secondSize)
            {
                (second, secondSize) = (other.Index, size);
            }
        }

        record.Occluder0 = Occluder(first, body, toBody);
        record.Occluder1 = Occluder(second, body, toBody);

        if (_star >= 0 && _bodies[_star].Posed)
            record.Emission.W = (float)(_bodies[_star].Radius / (_bodies[_star].Centre - body.Centre).Length());
    }

    private Vector4 Occluder(int index, Body body, doubleQuat toBody)
    {
        if (index < 0)
            return Vector4.Zero;

        var other = _bodies[index];
        return new Vector4(ToFloat(toBody * (other.Centre - body.Centre)), (float)other.Radius);
    }

    /// <summary>The focus upgrade once it is on the GPU, else the base bake once it is; null until then.</summary>
    private ImpostorTexture? TextureOf(Body body)
    {
        if (body.Index == _focusTextureBody && _focusTexture is { Uploaded: true })
            return _focusTexture;

        return body.Texture is { Uploaded: true } ? body.Texture : null;
    }

    private VkDescriptorSet SetOf(Body body)
    {
        if (body.Kind == CelestialKind.Star)
            return _placeholderSet;

        return body.Index == _focusTextureBody && _focusTexture is { Uploaded: true } ? _focusSet : body.Set;
    }

    private void SortFarToNear(Span<int> group)
    {
        for (var i = 1; i < group.Length; i++)
        {
            var v = group[i];
            var j = i - 1;

            for (; j >= 0 && BodyOf(group[j]).Distance < BodyOf(v).Distance; j--)
                group[j + 1] = group[j];

            group[j + 1] = v;
        }
    }

    /// <summary>Replaces the large impostor with the focus body's (the old one is freed once no frame uses it).</summary>
    private void StartFocusBake()
    {
        if (_focusTexture is not null)
        {
            if (!_focusTexture.Baked)
                return;

            _retired.Add(new Retired(_frame, _focusTexture, _focusSet));
            _focusTexture = null;
        }

        var body = _bodies[_focus];
        _focusTextureBody = _focus;
        _focusTexture = new ImpostorTexture(_context, body.Generator, PlanetImpostor.FocusSize);
        _focusSet = _freeFocusSets.Pop();
        _renderer.WriteSet(_focusSet, _focusTexture);
        _focusTexture.QueueBake(_scheduler);
        _scheduler.ExecuteJobs();
    }

    private VkDescriptorSet NewSet(ImpostorTexture texture)
    {
        var set = _renderer.AllocateSet();
        _renderer.WriteSet(set, texture);
        return set;
    }

    private void Upload(CommandBuffer commands, ImpostorTexture texture)
    {
        if (texture.Uploaded || !texture.Baked)
            return;

        texture.RecordUpload(commands);
        _staging.Add((_frame, texture));
    }

    private void Pose(FlyCamera camera)
    {
        foreach (var body in _bodies)
        {
            body.Posed = _frames.TryRelativeRender(body.Surface, camera.Frame, out var pose);

            if (!body.Posed)
                continue;

            body.Centre = pose.Position;
            body.Axes = pose.Rotation;
            body.Distance = (pose.Position - camera.Position).Length();
        }
    }

    private static double Altitude(Body body) => body.Posed ? body.Distance - body.Radius : double.PositiveInfinity;

    /// <summary>Re-expresses the camera's pose in <paramref name="frame"/> at render time.</summary>
    private void MoveCamera(FlyCamera camera, FrameId frame)
    {
        if (camera.Frame == frame || !_frames.TryRelativeRender(camera.Frame, frame, out var pose))
            return;

        camera.Position = pose.TransformPoint(camera.Position);
        camera.Rotation = doubleQuat.Normalize(pose.Rotation * camera.Rotation);
        camera.Frame = frame;
    }

    private static Vector3 ToFloat(double3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    private static Quaternion ToFloat(doubleQuat q) => new((float)q.X, (float)q.Y, (float)q.Z, (float)q.W);

    public void Dispose()
    {
        _scheduler.AbortJobs(waitForCompletion: true);
        _renderer.Dispose();

        foreach (var body in _bodies)
            body.Texture?.Dispose();

        foreach (var retired in _retired)
            retired.Texture.Dispose();

        _focusTexture?.Dispose();
        _placeholder.Dispose();
    }
}
