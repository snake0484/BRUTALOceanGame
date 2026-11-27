using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.Numerics;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using BrutalGame.Engine.Rendering;
using BrutalGame.Ships;
using BrutalGame.Terrain;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// Draws baked hull bricks: one host-visible vertex and index buffer holding every brick, one DrawIndexed per brick with
/// its camera-relative origin (double, then cast) as a push constant. <see cref="Upload"/> runs only when a bake finishes
/// (on commit), waits for the device and rewrites both buffers; nothing is uploaded per frame. Moves to Ships.Rendering
/// with the indirect brick arena once ships draw in game.
/// <para>
/// Paint and decals are <see cref="HullProjector"/> boxes the fragment shader colours the surface with. Each brick has a
/// grid of about 2 m cells listing the boxes that reach them (built on commit), so a fragment tests only its cell's; the
/// boxes themselves are written camera-relative each frame (centres in double, then cast), a few kilobytes.
/// </para>
/// </summary>
public sealed unsafe class HullRenderer : IDisposable
{
    public const int PaletteSize = 32;
    private const int UniformAlignment = 256;
    private const int PushConstantSize = 112;

    /// <summary>Moving parts (joints' groups) the renderer can turn; group 0 is the ship's own.</summary>
    public const int MaxGroups = 17;

    /// <summary>Projector cells: about this size, and at most this many along each axis of a brick.</summary>
    private const double CellMetres = 2.0;
    private const int MaxCellsPerAxis = 16;
    private const int VertexStride = PackedTerrainVertex.SizeInBytes;
    private const int MaxProjectors = StrokeExpansion.MaxProjectors + 256;
    private const int ProjectorRegion = MaxProjectors * 96;

    private readonly Renderer _renderer;
    private readonly Device _device;
    private readonly GpuBuffer _uniforms;
    private readonly VkDescriptorSetLayout _setLayout;
    private readonly VkDescriptorPool _pool;
    private readonly VkDescriptorSet[] _sets = new VkDescriptorSet[Renderer.FramesInFlight];
    private readonly VkPipelineLayout _layout;
    private readonly VkPipeline _pipeline;
    private readonly VkPipeline _ghostPipeline;
    private readonly List<Draw> _draws = new();
    private readonly Vector4[] _palette = new Vector4[PaletteSize];
    private GpuBuffer? _vertices;
    private GpuBuffer? _indices;
    private readonly GpuBuffer _projectors;
    private GpuBuffer _lists;
    private readonly List<HullProjector> _boxes = new();
    private readonly List<uint> _listScratch = new();
    private readonly List<int> _brickBoxes = new();
    private readonly List<List<uint>> _buckets = new();
    private readonly DecalTextures _decals;
    private readonly FinishTextures _finishes;
    private readonly Vector4[] _slotFinish = new Vector4[PaletteSize];
    private readonly Vector4[] _slotColour = new Vector4[PaletteSize];
    private int _hullSlot = -1;

    private struct Draw
    {
        public Vector3d Origin;
        public int FirstIndex;
        public int IndexCount;
        public int VertexOffset;
        public Vector3d Min;
        public Vector3d Max;

        /// <summary>Where the brick's cell table starts in the lists buffer.</summary>
        public int Table;

        /// <summary>Cell grid dimensions packed 5 bits each (x, y, z); 0 when no box reaches the brick.</summary>
        public int Cells;

        public double CellSize;

        /// <summary>The moving part (joint group) the brick belongs to.</summary>
        public int Group;
    }

    private readonly double[] _groupRotation = new double[MaxGroups * 9];
    private readonly Vector3d[] _groupTranslation = new Vector3d[MaxGroups];

    /// <summary>
    /// Each moving part's transform this frame (p' = R p + t, ship space; rotations row-major, 9 per group). Bricks are
    /// drawn turned by their group's; paint, decals and finishes are worked out at rest, so they move with the part.
    /// </summary>
    public void SetPose(ReadOnlySpan<double> rotations, ReadOnlySpan<Vector3d> translations)
    {
        var groups = Math.Min(translations.Length, MaxGroups);
        rotations[..(groups * 9)].CopyTo(_groupRotation);
        translations[..groups].CopyTo(_groupTranslation);

        for (var g = groups; g < MaxGroups; g++)
            Identity(g);
    }

    private void Identity(int g)
    {
        _groupRotation.AsSpan(g * 9, 9).Clear();
        (_groupRotation[g * 9], _groupRotation[g * 9 + 4], _groupRotation[g * 9 + 8]) = (1, 1, 1);
        _groupTranslation[g] = default;
    }

    /// <summary>A projector as the shader reads it (std430, six vec4).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GpuProjector
    {
        public Vector4 Centre;  // xyz camera-relative, w kind
        public Vector4 U;       // xyz axis / half, w half (metres)
        public Vector4 V;
        public Vector4 N;       // w: paint's distance along the stroke at the centre
        public Vector4 Colour;  // rgb linear, a opacity
        public Vector4 Params;  // paint: spacing, line width, pattern; decal: w layer
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameUniforms
    {
        public Matrix4x4 ViewProj;
        public Vector4 SunDir;
        public Vector4 SkyZenith;
        public Vector4 SkyHorizon;
        public Palette Colours;
        public Vector4 Camera;     // xyz ship-space camera (float; designer ships are small)
        public Vector4 Hull;       // x: the hull's paint slot, -1 for the structural materials' colours
        public Palette SlotFinish; // per paint slot: finish layer (-1 none), tile metres, metalness scale, mean luminance
        public Palette SlotColour; // per paint slot: linear rgb, a opacity
    }

    [InlineArray(PaletteSize)]
    private struct Palette
    {
        private Vector4 _element;
    }

    public HullRenderer(Renderer renderer, DecalTextures decals, FinishTextures finishes)
    {
        _renderer = renderer;
        _decals = decals;
        _finishes = finishes;
        var context = renderer.Context;
        _device = context.Device;
        _uniforms = new GpuBuffer(context, UniformStride * Renderer.FramesInFlight, VkBufferUsageFlags.UniformBufferBit, true);
        _projectors = new GpuBuffer(context, ProjectorRegion * Renderer.FramesInFlight, VkBufferUsageFlags.StorageBufferBit, true);
        _lists = new GpuBuffer(context, 1 << 16, VkBufferUsageFlags.StorageBufferBit, true);
        _setLayout = CreateSetLayout();
        _pool = CreatePool();
        CreateSets();

        Span<VkPushConstantRange> push =
        [
            new VkPushConstantRange { StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, Size = (ByteSize32)PushConstantSize },
        ];
        _layout = _device.CreatePipelineLayout([_setLayout], push, null);
        _pipeline = CreatePipeline(context.Pipelines, renderer.RenderPass, ghost: false);
        _ghostPipeline = CreatePipeline(context.Pipelines, renderer.RenderPass, ghost: true);
        _palette.AsSpan().Fill(new Vector4(0.6f, 0.6f, 0.6f, 1f));
        SetPaint(null, _ => -1);

        for (var g = 0; g < MaxGroups; g++)
            Identity(g);
    }

    public int BrickCount => _draws.Count;

    public long VertexBytes => _vertices?.Size ?? 0;

    public long IndexBytes => _indices?.Size ?? 0;

    /// <summary>Paint and decal boxes drawn, and the brick list entries pointing at them.</summary>
    public int Projectors => _boxes.Count;

    public int ProjectorRefs { get; private set; }

    private static int UniformStride => (sizeof(FrameUniforms) + UniformAlignment - 1) / UniformAlignment * UniformAlignment;

    /// <summary>Linear albedo per plan material index; entries past <see cref="PaletteSize"/> are ignored.</summary>
    /// <summary>Linear albedo and opacity per plan material index (opacity below 1: glazing, drawn by <see cref="RecordGlazing"/>).</summary>
    public void SetPalette(ReadOnlySpan<Vector4> colours)
    {
        HasGlazing = false;

        for (var i = 0; i < Math.Min(colours.Length, PaletteSize); i++)
        {
            _palette[i] = colours[i];
            HasGlazing |= colours[i].W < 0.999f;
        }
    }

    /// <summary>Some palette entry is see-through, so the glazing pass has something to draw.</summary>
    public bool HasGlazing { get; private set; }

    /// <summary>Replaces the drawn bricks. Waits for the device: call on commit only.</summary>
    public void Upload(IReadOnlyList<HullBrick> bricks) => Upload(bricks, null);

    /// <summary>Replaces the drawn bricks, each in moving part <paramref name="groups"/>[i] (all the ship's own when null). Waits for the device: call on commit only.</summary>
    public void Upload(IReadOnlyList<HullBrick> bricks, IReadOnlyList<int>? groups)
    {
        var vertexCount = 0;
        var indexCount = 0;

        foreach (var brick in bricks)
        {
            vertexCount += brick.VertexCount;
            indexCount += brick.IndexCount;
        }

        _renderer.WaitIdle();
        EnsureCapacity(ref _vertices, Math.Max(1, vertexCount) * VertexStride, VkBufferUsageFlags.VertexBufferBit);
        EnsureCapacity(ref _indices, Math.Max(1, indexCount) * sizeof(uint), VkBufferUsageFlags.IndexBufferBit);
        _draws.Clear();

        var vertices = _vertices!.AsSpan<PackedTerrainVertex>();
        var indices = _indices!.AsSpan<uint>();
        var v = 0;
        var i = 0;

        for (var b = 0; b < bricks.Count; b++)
        {
            var brick = bricks[b];

            if (brick.IndexCount == 0)
                continue;

            brick.VertexSpan.CopyTo(vertices[v..]);
            brick.IndexSpan.CopyTo(indices[i..]);
            Bounds(brick, out var min, out var max);
            var group = groups is null ? 0 : Math.Clamp(groups[b], 0, MaxGroups - 1);
            _draws.Add(new Draw { Origin = brick.Origin, FirstIndex = i, IndexCount = brick.IndexCount, VertexOffset = v, Min = min, Max = max, Group = group });
            v += brick.VertexCount;
            i += brick.IndexCount;
        }

        BuildLists();
    }

    /// <summary>
    /// The plan's paint slots (finish layers from <paramref name="finishLayer"/>, -1 when the finish is not in game data) and
    /// the hull's slot (-1: the structural materials' colours). Cheap: read into the frame uniforms.
    /// </summary>
    public void SetPaint(HullPaintSet? paint, Func<uint, int> finishLayer)
    {
        Array.Fill(_slotFinish, new Vector4(-1f, 1f, 0f, 1f));
        Array.Fill(_slotColour, new Vector4(0.7f, 0.7f, 0.7f, 1f));
        _hullSlot = -1;

        if (paint is null)
            return;

        for (var i = 0; i < Math.Min(paint.Slots.Count, PaletteSize); i++)
        {
            var slot = paint.Slots[i];
            var layer = finishLayer(slot.Finish);
            var info = layer >= 0 && layer < _finishes.Info.Length ? _finishes.Info[layer] : new Vector4(1f, 0f, 1f, 0f);
            _slotFinish[i] = new Vector4(layer, info.X, info.Y, info.Z);
            _slotColour[i] = Linear(slot.Colour);
        }

        _hullSlot = paint.Hull < Math.Min(paint.Slots.Count, PaletteSize) ? paint.Hull : -1;
    }

    /// <summary>Replaces the paint and decal boxes (ship space). Waits for the device: call on commit only.</summary>
    public void SetProjectors(IReadOnlyList<HullProjector> boxes)
    {
        _renderer.WaitIdle();
        _boxes.Clear();

        for (var i = 0; i < boxes.Count && _boxes.Count < MaxProjectors; i++)
            _boxes.Add(boxes[i]);

        BuildLists();
    }

    /// <summary>A brick's ship-space bounds from its packed vertices.</summary>
    private static void Bounds(HullBrick brick, out Vector3d min, out Vector3d max)
    {
        const double scale = HullBins.CellMetres / PackedTerrainVertex.StepsPerVoxel;
        const double offset = PackedTerrainVertex.OffsetVoxels * PackedTerrainVertex.StepsPerVoxel;
        int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue, z1 = int.MinValue;

        foreach (var p in brick.VertexSpan)
        {
            (x0, y0, z0) = (Math.Min(x0, p.X), Math.Min(y0, p.Y), Math.Min(z0, p.Z));
            (x1, y1, z1) = (Math.Max(x1, p.X), Math.Max(y1, p.Y), Math.Max(z1, p.Z));
        }

        var o = brick.Origin;
        min = new Vector3d(o.X + (x0 - offset) * scale, o.Y + (y0 - offset) * scale, o.Z + (z0 - offset) * scale);
        max = new Vector3d(o.X + (x1 - offset) * scale, o.Y + (y1 - offset) * scale, o.Z + (z1 - offset) * scale);
    }

    /// <summary>
    /// Each brick's grid of cells (about <see cref="CellMetres"/> across, at most <see cref="MaxCellsPerAxis"/> a side), each
    /// listing the boxes whose bounds reach it, in box order (decals later in the list draw on top). Laid out per brick as a
    /// table of (first, count) per cell followed by the cells' box indices; a fragment reads only its own cell's list.
    /// </summary>
    private void BuildLists()
    {
        _listScratch.Clear();
        var bounds = new (Vector3d Min, Vector3d Max)[_boxes.Count];

        for (var b = 0; b < _boxes.Count; b++)
        {
            _boxes[b].Bounds(out var min, out var max);
            bounds[b] = (min, max);
        }

        for (var d = 0; d < _draws.Count; d++)
        {
            var draw = _draws[d];
            _brickBoxes.Clear();

            for (var b = 0; b < _boxes.Count; b++)
            {
                var (min, max) = bounds[b];

                if (min.X <= draw.Max.X && max.X >= draw.Min.X && min.Y <= draw.Max.Y && max.Y >= draw.Min.Y && min.Z <= draw.Max.Z && max.Z >= draw.Min.Z)
                    _brickBoxes.Add(b);
            }

            draw.Cells = 0;

            if (_brickBoxes.Count > 0)
                Grid(ref draw, bounds);

            _draws[d] = draw;
        }

        ProjectorRefs = _listScratch.Count;
        var bytes = Math.Max(_listScratch.Count, 1) * sizeof(uint);

        if (bytes > _lists.Size)
        {
            _renderer.WaitIdle();
            _lists.Dispose();
            var size = _lists.Size;

            while (size < bytes)
                size *= 2;

            _lists = new GpuBuffer(_renderer.Context, size, VkBufferUsageFlags.StorageBufferBit, true);
            WriteSets();
        }

        CollectionsMarshal.AsSpan(_listScratch).CopyTo(_lists.AsSpan<uint>());
    }

    /// <summary>The brick's cell grid over its bounds and the lists of <see cref="_brickBoxes"/> in each cell.</summary>
    private void Grid(ref Draw draw, (Vector3d Min, Vector3d Max)[] bounds)
    {
        var extent = Sub(draw.Max, draw.Min);
        var cell = Math.Max(CellMetres, Math.Max(extent.X, Math.Max(extent.Y, extent.Z)) / MaxCellsPerAxis);
        int nx = Dim(extent.X), ny = Dim(extent.Y), nz = Dim(extent.Z);
        var cells = nx * ny * nz;

        while (_buckets.Count < cells)
            _buckets.Add(new List<uint>());

        for (var c = 0; c < cells; c++)
            _buckets[c].Clear();

        foreach (var b in _brickBoxes)
        {
            var (min, max) = bounds[b];
            int x0 = Index(min.X - draw.Min.X, nx), x1 = Index(max.X - draw.Min.X, nx);
            int y0 = Index(min.Y - draw.Min.Y, ny), y1 = Index(max.Y - draw.Min.Y, ny);
            int z0 = Index(min.Z - draw.Min.Z, nz), z1 = Index(max.Z - draw.Min.Z, nz);

            for (var z = z0; z <= z1; z++)
            {
                for (var y = y0; y <= y1; y++)
                {
                    for (var x = x0; x <= x1; x++)
                        _buckets[x + nx * (y + ny * z)].Add((uint)b);
                }
            }
        }

        // The table, then each cell's list.
        draw.Table = _listScratch.Count;
        draw.Cells = nx | (ny << 5) | (nz << 10);
        draw.CellSize = cell;

        for (var c = 0; c < cells * 2; c++)
            _listScratch.Add(0);

        for (var c = 0; c < cells; c++)
        {
            _listScratch[draw.Table + c * 2] = (uint)_listScratch.Count;
            _listScratch[draw.Table + c * 2 + 1] = (uint)_buckets[c].Count;
            _listScratch.AddRange(_buckets[c]);
        }

        int Dim(double size) => Math.Clamp((int)Math.Ceiling(size / cell), 1, MaxCellsPerAxis);

        int Index(double offset, int n) => Math.Clamp((int)Math.Floor(offset / cell), 0, n - 1);
    }

    private static Vector3d Sub(Vector3d a, Vector3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    /// <summary>This frame's boxes, camera-relative.</summary>
    private void WriteProjectors(int frameIndex, double3 camera)
    {
        var target = new Span<GpuProjector>((byte*)_projectors.Mapped + frameIndex * ProjectorRegion, MaxProjectors);

        for (var i = 0; i < _boxes.Count; i++)
        {
            var b = _boxes[i];
            target[i] = new GpuProjector
            {
                Centre = new Vector4((float)(b.Centre.X - camera.X), (float)(b.Centre.Y - camera.Y), (float)(b.Centre.Z - camera.Z), (float)b.Kind),
                U = Axis(b.U, b.HalfU),
                V = Axis(b.V, b.HalfV),
                N = Axis(b.N, b.HalfN) with { W = (float)b.Along },
                Colour = Linear(b.Colour),
                Params = b.Kind == ProjectorKind.Paint
                    ? new Vector4(b.Spacing, b.LineWidth, (float)b.Pattern, b.Layer)
                    : new Vector4((float)b.Shape, b.Paint, 0f, b.Layer),
            };
        }
    }

    private static Vector4 Axis(Vector3d axis, double half) =>
        new((float)(axis.X / half), (float)(axis.Y / half), (float)(axis.Z / half), (float)half);

    private static Vector4 Linear(uint srgb)
    {
        static float Channel(uint c) => MathF.Pow(c / 255f, 2.2f);
        return new Vector4(Channel(srgb & 0xFF), Channel((srgb >> 8) & 0xFF), Channel((srgb >> 16) & 0xFF), (srgb >> 24) / 255f);
    }

    /// <summary>
    /// Cutaway: fragments with n·p &gt; d (ship metres) are not drawn. Off when <paramref name="axis"/> is negative.
    /// </summary>
    public void SetCutaway(int axis, float offset, bool flip)
    {
        _clipAxis = axis;
        _clipOffset = offset;
        _clipFlip = flip;
    }

    private int _clipAxis = -1;
    private float _clipOffset;
    private bool _clipFlip;

    /// <summary>Inside the opaque pass. <paramref name="viewProj"/> has no translation; <paramref name="camera"/> is in ship metres.</summary>
    public void Record(CommandBuffer commands, int frameIndex, VkExtent2D extent, in Matrix4x4 viewProj, double3 camera, in SceneLighting lighting) =>
        Record(commands, frameIndex, extent, viewProj, camera, lighting, 0f);

    /// <summary>
    /// The interior view: the hull see-through at <paramref name="opacity"/>, both sides of every face, blended and writing
    /// no depth. In the second scene pass (after the opaque one), instead of <see cref="Record(CommandBuffer, int, VkExtent2D, in Matrix4x4, double3, in SceneLighting)"/>.
    /// </summary>
    public void RecordGhost(CommandBuffer commands, int frameIndex, VkExtent2D extent, in Matrix4x4 viewProj, double3 camera, in SceneLighting lighting, float opacity) =>
        Record(commands, frameIndex, extent, viewProj, camera, lighting, Math.Max(opacity, 1e-3f));

    /// <summary>Windows and other glazing: blended, writing no depth, after the opaque hull in the second scene pass.</summary>
    public void RecordGlazing(CommandBuffer commands, int frameIndex, VkExtent2D extent, in Matrix4x4 viewProj, double3 camera, in SceneLighting lighting)
    {
        if (HasGlazing)
            Record(commands, frameIndex, extent, viewProj, camera, lighting, 0f, glazing: true);
    }

    private void Record(CommandBuffer commands, int frameIndex, VkExtent2D extent, in Matrix4x4 viewProj, double3 camera, in SceneLighting lighting, float ghost, bool glazing = false)
    {
        if (_draws.Count == 0)
            return;

        var uniforms = (FrameUniforms*)((byte*)_uniforms.Mapped + frameIndex * UniformStride);
        uniforms->ViewProj = viewProj;
        uniforms->SunDir = new Vector4(lighting.SunDirection, lighting.SunIntensity);
        uniforms->SkyZenith = new Vector4(lighting.SkyZenith, 0f);
        uniforms->SkyHorizon = new Vector4(lighting.SkyHorizon, 0f);

        for (var p = 0; p < PaletteSize; p++)
        {
            uniforms->Colours[p] = _palette[p];
            uniforms->SlotFinish[p] = _slotFinish[p];
            uniforms->SlotColour[p] = _slotColour[p];
        }

        uniforms->Camera = new Vector4((float)camera.X, (float)camera.Y, (float)camera.Z, 0f);
        uniforms->Hull = new Vector4(_hullSlot, 0f, 0f, 0f);

        if (ghost <= 0f && !glazing)
            WriteProjectors(frameIndex, camera);

        commands.BindPipeline(VkPipelineBindPoint.Graphics, ghost > 0f || glazing ? _ghostPipeline : _pipeline);
        commands.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 0, [_sets[frameIndex]], []);
        commands.SetViewport(0, [new VkViewport { Width = extent.Width, Height = extent.Height, MaxDepth = 1f }]);
        commands.SetScissor(0, [new VkRect2D { Extent = extent }]);
        commands.BindVertexBuffers(0, [_vertices!.Handle], [(ByteSize64)0]);
        commands.BindIndexBuffer(_indices!.Handle, (ByteSize64)0, VkIndexType.UInt32);

        const float scale = HullBins.CellMetres / PackedTerrainVertex.StepsPerVoxel;

        // The clip plane in camera-relative space: n·(rel + camera) > d  ⇔  n·rel > d − n·camera.
        var clip = new Vector4(0f, 0f, 0f, float.MaxValue);

        if (_clipAxis >= 0)
        {
            var sign = _clipFlip ? -1f : 1f;
            var cam = _clipAxis == 0 ? camera.X : _clipAxis == 1 ? camera.Y : camera.Z;
            clip = new Vector4(_clipAxis == 0 ? sign : 0f, _clipAxis == 1 ? sign : 0f, _clipAxis == 2 ? sign : 0f, (float)(sign * (_clipOffset - cam)));
        }

        var push = stackalloc Vector4[7];
        push[1] = clip;
        push[2] = new Vector4(ghost, glazing ? 1f : 0f, 0f, 0f);

        foreach (var draw in _draws)
        {
            push[0] = new Vector4((float)(draw.Origin.X - camera.X), (float)(draw.Origin.Y - camera.Y), (float)(draw.Origin.Z - camera.Z), scale);
            var boxes = ghost <= 0f && !glazing && draw.Cells != 0;
            push[2].Z = boxes ? draw.Table : 0f;
            push[2].W = boxes ? draw.Cells : 0f;
            push[3] = new Vector4((float)(draw.Min.X - camera.X), (float)(draw.Min.Y - camera.Y), (float)(draw.Min.Z - camera.Z), (float)draw.CellSize);

            // The part's turn: world = R rest + d, camera-relative, with d = R cam + t - cam (in double).
            var r = _groupRotation.AsSpan(draw.Group * 9, 9);
            var t = _groupTranslation[draw.Group];
            var dx = r[0] * camera.X + r[1] * camera.Y + r[2] * camera.Z + t.X - camera.X;
            var dy = r[3] * camera.X + r[4] * camera.Y + r[5] * camera.Z + t.Y - camera.Y;
            var dz = r[6] * camera.X + r[7] * camera.Y + r[8] * camera.Z + t.Z - camera.Z;
            push[4] = new Vector4((float)r[0], (float)r[1], (float)r[2], (float)dx);
            push[5] = new Vector4((float)r[3], (float)r[4], (float)r[5], (float)dy);
            push[6] = new Vector4((float)r[6], (float)r[7], (float)r[8], (float)dz);
            commands.PushConstants(_layout, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, 0, new ReadOnlySpan<byte>(push, PushConstantSize));
            commands.DrawIndexed(draw.IndexCount, 1, draw.FirstIndex, draw.VertexOffset, 0);
        }
    }

    private void EnsureCapacity(ref GpuBuffer? buffer, int bytes, VkBufferUsageFlags usage)
    {
        if (buffer is not null && buffer.Size >= bytes)
            return;

        buffer?.Dispose();
        var size = 1 << 16;

        while (size < bytes)
            size *= 2;

        buffer = new GpuBuffer(_renderer.Context, size, usage, hostVisible: true);
    }

    /// <summary>0 frame uniforms, 1 projectors, 2 brick lists, 3 decal images, 4 finish albedo, 5 finish surface.</summary>
    private VkDescriptorSetLayout CreateSetLayout()
    {
        var bindings = stackalloc VkDescriptorSetLayoutBinding[]
        {
            new() { Binding = 0, DescriptorType = VkDescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit },
            new() { Binding = 1, DescriptorType = VkDescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit },
            new() { Binding = 2, DescriptorType = VkDescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit },
            new() { Binding = 3, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit },
            new() { Binding = 4, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit },
            new() { Binding = 5, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit },
        };

        return _device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = 6, Bindings = bindings }, null);
    }

    private VkDescriptorPool CreatePool()
    {
        var sizes = stackalloc VkDescriptorPoolSize[]
        {
            new() { Type = VkDescriptorType.UniformBuffer, DescriptorCount = Renderer.FramesInFlight },
            new() { Type = VkDescriptorType.StorageBuffer, DescriptorCount = 2 * Renderer.FramesInFlight },
            new() { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 3 * Renderer.FramesInFlight },
        };
        return _device.CreateDescriptorPool(new VkDescriptorPoolCreateInfo { MaxSets = Renderer.FramesInFlight, PoolSizeCount = 3, PoolSizes = sizes }, null);
    }

    private void CreateSets()
    {
        var layouts = stackalloc VkDescriptorSetLayout[Renderer.FramesInFlight];

        for (var i = 0; i < Renderer.FramesInFlight; i++)
            layouts[i] = _setLayout;

        _device.AllocateDescriptorSets(
            new VkDescriptorSetAllocateInfo { DescriptorPool = _pool, DescriptorSetCount = Renderer.FramesInFlight, SetLayouts = layouts },
            _sets
        );

        WriteSets();
    }

    private void WriteSets()
    {
        for (var i = 0; i < Renderer.FramesInFlight; i++)
        {
            var uniforms = new VkDescriptorBufferInfo { Buffer = _uniforms.Handle, Offset = (ByteSize64)(i * UniformStride), Range = (ByteSize64)sizeof(FrameUniforms) };
            var projectors = new VkDescriptorBufferInfo { Buffer = _projectors.Handle, Offset = (ByteSize64)(i * ProjectorRegion), Range = (ByteSize64)ProjectorRegion };
            var lists = new VkDescriptorBufferInfo { Buffer = _lists.Handle, Offset = (ByteSize64)0, Range = (ByteSize64)_lists.Size };
            var image = new VkDescriptorImageInfo { ImageView = _decals.View, Sampler = _decals.Sampler, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            var albedo = new VkDescriptorImageInfo { ImageView = _finishes.AlbedoView, Sampler = _finishes.Sampler, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            var surface = new VkDescriptorImageInfo { ImageView = _finishes.SurfaceView, Sampler = _finishes.Sampler, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };

            _device.UpdateDescriptorSets(
                [
                    new VkWriteDescriptorSet { DstSet = _sets[i], DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.UniformBuffer, BufferInfo = &uniforms },
                    new VkWriteDescriptorSet { DstSet = _sets[i], DstBinding = 1, DescriptorCount = 1, DescriptorType = VkDescriptorType.StorageBuffer, BufferInfo = &projectors },
                    new VkWriteDescriptorSet { DstSet = _sets[i], DstBinding = 2, DescriptorCount = 1, DescriptorType = VkDescriptorType.StorageBuffer, BufferInfo = &lists },
                    new VkWriteDescriptorSet { DstSet = _sets[i], DstBinding = 3, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &image },
                    new VkWriteDescriptorSet { DstSet = _sets[i], DstBinding = 4, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &albedo },
                    new VkWriteDescriptorSet { DstSet = _sets[i], DstBinding = 5, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &surface },
                ],
                []
            );
        }
    }

    private VkPipeline CreatePipeline(PipelineCache pipelines, VkRenderPass renderPass, bool ghost)
    {
        var vert = ShaderLoader.Load(_device, "hull.vert", ShaderKind.VertexShader);
        var frag = ShaderLoader.Load(_device, "hull.frag", ShaderKind.FragmentShader);

        try
        {
            var entry = "main"u8;
            fixed (byte* name = entry)
            {
                var stages = stackalloc VkPipelineShaderStageCreateInfo[]
                {
                    new() { Stage = VkShaderStageFlags.VertexBit, Module = vert, Name = name },
                    new() { Stage = VkShaderStageFlags.FragmentBit, Module = frag, Name = name },
                };
                var binding = new VkVertexInputBindingDescription
                {
                    Binding = 0,
                    Stride = (ByteSize32)VertexStride,
                    InputRate = VkVertexInputRate.Vertex,
                };
                var attributes = stackalloc VkVertexInputAttributeDescription[]
                {
                    new() { Location = 0, Binding = 0, Format = VkFormat.R16G16B16A16UInt, Offset = (ByteSize32)0 },
                    new() { Location = 1, Binding = 0, Format = VkFormat.R16G16SNorm, Offset = (ByteSize32)8 },
                };
                var vertexInput = new VkPipelineVertexInputStateCreateInfo
                {
                    VertexBindingDescriptionCount = 1,
                    VertexBindingDescriptions = &binding,
                    VertexAttributeDescriptionCount = 2,
                    VertexAttributeDescriptions = attributes,
                };
                var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo { Topology = VkPrimitiveTopology.TriangleList };
                var viewport = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, ScissorCount = 1 };
                var raster = new VkPipelineRasterizationStateCreateInfo
                {
                    PolygonMode = VkPolygonMode.Fill,
                    CullMode = VkCullModeFlags.None,
                    FrontFace = VkFrontFace.CounterClockwise,
                    LineWidth = 1f,
                };
                var multisample = new VkPipelineMultisampleStateCreateInfo { RasterizationSamples = VkSampleCountFlags._1Bit };
                var depth = new VkPipelineDepthStencilStateCreateInfo
                {
                    DepthTestEnable = true,
                    DepthWriteEnable = !ghost,
                    DepthCompareOp = VkCompareOp.Greater,
                };
                var blendAttachment = new VkPipelineColorBlendAttachmentState
                {
                    BlendEnable = ghost,
                    SrcColorBlendFactor = VkBlendFactor.SrcAlpha,
                    DstColorBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                    ColorBlendOp = VkBlendOp.Add,
                    SrcAlphaBlendFactor = VkBlendFactor.One,
                    DstAlphaBlendFactor = VkBlendFactor.OneMinusSrcAlpha,
                    AlphaBlendOp = VkBlendOp.Add,
                    ColorWriteMask = VkColorComponentFlags.RBit | VkColorComponentFlags.GBit | VkColorComponentFlags.BBit | VkColorComponentFlags.ABit,
                };
                var blend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &blendAttachment };
                var dynamicStates = stackalloc VkDynamicState[] { VkDynamicState.Viewport, VkDynamicState.Scissor };
                var dynamic = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = 2, DynamicStates = dynamicStates };

                return pipelines.CreateGraphicsPipeline(
                    new VkGraphicsPipelineCreateInfo
                    {
                        StageCount = 2,
                        Stages = stages,
                        VertexInputState = &vertexInput,
                        InputAssemblyState = &inputAssembly,
                        ViewportState = &viewport,
                        RasterizationState = &raster,
                        MultisampleState = &multisample,
                        DepthStencilState = &depth,
                        ColorBlendState = &blend,
                        DynamicState = &dynamic,
                        Layout = _layout,
                        RenderPass = renderPass,
                    }
                );
            }
        }
        finally
        {
            _device.DestroyShaderModule(vert, null);
            _device.DestroyShaderModule(frag, null);
        }
    }

    public void Dispose()
    {
        _device.WaitIdle();
        _device.DestroyPipeline(_pipeline, null);
        _device.DestroyPipeline(_ghostPipeline, null);
        _device.DestroyPipelineLayout(_layout, null);
        _device.DestroyDescriptorPool(_pool, null);
        _device.DestroyDescriptorSetLayout(_setLayout, null);
        _vertices?.Dispose();
        _indices?.Dispose();
        _uniforms.Dispose();
        _projectors.Dispose();
        _lists.Dispose();
    }
}
