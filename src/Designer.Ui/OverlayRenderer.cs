using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.Numerics;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using BrutalGame.Engine.Rendering;
using BrutalGame.Ships;

namespace BrutalGame.Designer.Ui;

/// <summary>
/// The designer's 3D overlay: lines and translucent triangles (brush outlines, gizmos, mesh cages, drag ghosts) depth
/// tested against the hull, drawn after it in the second scene pass. Each primitive is drawn twice: where it is in front
/// (full colour, a small depth pull so lines on the surface win) and where the hull hides it (faint), so gizmos and
/// selections stay readable through the hull. Geometry is rebuilt on the CPU each frame into a per-frame host-visible
/// buffer (camera-relative floats from double positions); no descriptor sets, the view-projection rides in push constants.
/// Thick lines are camera-facing ribbons, since wide lines are an optional device feature.
/// </summary>
public sealed unsafe class OverlayRenderer : IDisposable
{
    public const int MaxVertices = 1 << 18;
    private const float DepthPull = 1.004f;
    private const float HiddenAlpha = 0.22f;
    private const int PushBytes = 80;

    private readonly Renderer _renderer;
    private readonly Device _device;
    private readonly GpuBuffer _buffer;
    private readonly VkPipelineLayout _layout;
    private readonly VkPipeline _triVisible, _triHidden, _lineVisible, _lineHidden;
    private readonly List<Vertex> _tris = new(4096);
    private readonly List<Vertex> _lines = new(4096);
    private double3 _camera;

    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public Vector3 Position;
        public uint Colour;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Push
    {
        public Matrix4x4 ViewProj;
        public Vector4 Params;
    }

    public OverlayRenderer(Renderer renderer)
    {
        _renderer = renderer;
        _device = renderer.Context.Device;
        _buffer = new GpuBuffer(renderer.Context, MaxVertices * sizeof(Vertex) * Renderer.FramesInFlight, VkBufferUsageFlags.VertexBufferBit, hostVisible: true);
        Span<VkPushConstantRange> push = [new VkPushConstantRange { StageFlags = VkShaderStageFlags.VertexBit, Size = (ByteSize32)PushBytes }];
        _layout = _device.CreatePipelineLayout([], push, null);

        var vert = ShaderLoader.Load(_device, "overlay.vert", ShaderKind.VertexShader);
        var frag = ShaderLoader.Load(_device, "overlay.frag", ShaderKind.FragmentShader);

        try
        {
            var pass = renderer.RenderPass;
            _triVisible = CreatePipeline(vert, frag, pass, VkPrimitiveTopology.TriangleList, VkCompareOp.GreaterOrEqual);
            _triHidden = CreatePipeline(vert, frag, pass, VkPrimitiveTopology.TriangleList, VkCompareOp.Less);
            _lineVisible = CreatePipeline(vert, frag, pass, VkPrimitiveTopology.LineList, VkCompareOp.GreaterOrEqual);
            _lineHidden = CreatePipeline(vert, frag, pass, VkPrimitiveTopology.LineList, VkCompareOp.Less);
        }
        finally
        {
            _device.DestroyShaderModule(vert, null);
            _device.DestroyShaderModule(frag, null);
        }
    }

    public int VertexCount => _tris.Count + _lines.Count;

    public static uint Rgba(byte r, byte g, byte b, byte a = 255) => r | ((uint)g << 8) | ((uint)b << 16) | ((uint)a << 24);

    public static uint WithAlpha(uint rgba, byte a) => (rgba & 0x00FFFFFF) | ((uint)a << 24);

    /// <summary>Starts a frame's overlay; positions are given in ship metres and drawn relative to <paramref name="camera"/>.</summary>
    public void Begin(double3 camera)
    {
        _camera = camera;
        _tris.Clear();
        _lines.Clear();
    }

    private Vector3 Rel(Vector3d p) => new((float)(p.X - _camera.X), (float)(p.Y - _camera.Y), (float)(p.Z - _camera.Z));

    public void Line(Vector3d a, Vector3d b, uint rgba)
    {
        if (_lines.Count + _tris.Count + 2 > MaxVertices)
            return;

        _lines.Add(new Vertex { Position = Rel(a), Colour = rgba });
        _lines.Add(new Vertex { Position = Rel(b), Colour = rgba });
    }

    public void Triangle(Vector3d a, Vector3d b, Vector3d c, uint rgba)
    {
        if (_lines.Count + _tris.Count + 3 > MaxVertices)
            return;

        _tris.Add(new Vertex { Position = Rel(a), Colour = rgba });
        _tris.Add(new Vertex { Position = Rel(b), Colour = rgba });
        _tris.Add(new Vertex { Position = Rel(c), Colour = rgba });
    }

    /// <summary>A camera-facing ribbon <paramref name="pixels"/> wide at the given metres-per-pixel scale (thick line).</summary>
    public void Ribbon(Vector3d a, Vector3d b, double width, uint rgba)
    {
        var ra = Rel(a);
        var rb = Rel(b);
        var mid = (ra + rb) * 0.5f;
        var side = Vector3.Cross(rb - ra, mid);
        var length = side.Length();

        if (length < 1e-9f)
            return;

        side *= (float)(width * 0.5) / length;
        Quad(ra - side, rb - side, rb + side, ra + side, rgba);
    }

    private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, uint rgba)
    {
        if (_lines.Count + _tris.Count + 6 > MaxVertices)
            return;

        _tris.Add(new Vertex { Position = a, Colour = rgba });
        _tris.Add(new Vertex { Position = b, Colour = rgba });
        _tris.Add(new Vertex { Position = c, Colour = rgba });
        _tris.Add(new Vertex { Position = a, Colour = rgba });
        _tris.Add(new Vertex { Position = c, Colour = rgba });
        _tris.Add(new Vertex { Position = d, Colour = rgba });
    }

    /// <summary>An axis-aligned cube of half size <paramref name="half"/> metres (gizmo handles, vertex markers).</summary>
    public void Cube(Vector3d centre, double half, uint rgba)
    {
        Span<Vector3d> c = stackalloc Vector3d[8];

        for (var i = 0; i < 8; i++)
            c[i] = new Vector3d(centre.X + ((i & 1) != 0 ? half : -half), centre.Y + ((i & 2) != 0 ? half : -half), centre.Z + ((i & 4) != 0 ? half : -half));

        ReadOnlySpan<byte> faces = [1, 3, 7, 5, 0, 4, 6, 2, 2, 6, 7, 3, 0, 1, 5, 4, 4, 5, 7, 6, 0, 2, 3, 1];

        for (var f = 0; f < faces.Length; f += 4)
        {
            Triangle(c[faces[f]], c[faces[f + 1]], c[faces[f + 2]], rgba);
            Triangle(c[faces[f]], c[faces[f + 2]], c[faces[f + 3]], rgba);
        }
    }

    /// <summary>A cone from <paramref name="base"/> to <paramref name="tip"/> (gizmo arrow heads).</summary>
    public void Cone(Vector3d @base, Vector3d tip, double radius, uint rgba)
    {
        const int sides = 10;
        var axis = new Vector3d(tip.X - @base.X, tip.Y - @base.Y, tip.Z - @base.Z);
        var helper = Math.Abs(axis.Y) < 0.9 * axis.Length() ? new Vector3d(0, 1, 0) : new Vector3d(1, 0, 0);
        var u = Normalize(Cross(axis, helper));
        var v = Normalize(Cross(axis, u));

        for (var i = 0; i < sides; i++)
        {
            double a0 = i * Math.Tau / sides, a1 = (i + 1) * Math.Tau / sides;
            var p0 = new Vector3d(@base.X + (u.X * Math.Cos(a0) + v.X * Math.Sin(a0)) * radius, @base.Y + (u.Y * Math.Cos(a0) + v.Y * Math.Sin(a0)) * radius, @base.Z + (u.Z * Math.Cos(a0) + v.Z * Math.Sin(a0)) * radius);
            var p1 = new Vector3d(@base.X + (u.X * Math.Cos(a1) + v.X * Math.Sin(a1)) * radius, @base.Y + (u.Y * Math.Cos(a1) + v.Y * Math.Sin(a1)) * radius, @base.Z + (u.Z * Math.Cos(a1) + v.Z * Math.Sin(a1)) * radius);
            Triangle(p0, p1, tip, rgba);
            Triangle(p1, p0, @base, rgba);
        }
    }

    private static Vector3d Cross(Vector3d a, Vector3d b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    private static Vector3d Normalize(Vector3d v)
    {
        var l = v.Length();
        return l > 0 ? new Vector3d(v.X / l, v.Y / l, v.Z / l) : new Vector3d(1, 0, 0);
    }

    /// <summary>In the second scene pass (after the hull): hidden parts faint, then visible parts.</summary>
    public void Record(CommandBuffer commands, int frameIndex, VkExtent2D extent, in Matrix4x4 viewProj)
    {
        var count = _tris.Count + _lines.Count;

        if (count == 0)
            return;

        var mapped = new Span<Vertex>((byte*)_buffer.Mapped + frameIndex * MaxVertices * sizeof(Vertex), MaxVertices);
        CollectionsMarshal.AsSpan(_tris).CopyTo(mapped);
        CollectionsMarshal.AsSpan(_lines).CopyTo(mapped[_tris.Count..]);

        commands.SetViewport(0, [new VkViewport { Width = extent.Width, Height = extent.Height, MaxDepth = 1f }]);
        commands.SetScissor(0, [new VkRect2D { Extent = extent }]);
        commands.BindVertexBuffers(0, [_buffer.Handle], [(ByteSize64)(frameIndex * MaxVertices * sizeof(Vertex))]);

        Draw(commands, _triHidden, viewProj, DepthPull, HiddenAlpha, 0, _tris.Count);
        Draw(commands, _lineHidden, viewProj, DepthPull, HiddenAlpha, _tris.Count, _lines.Count);
        Draw(commands, _triVisible, viewProj, DepthPull, 1f, 0, _tris.Count);
        Draw(commands, _lineVisible, viewProj, DepthPull, 1f, _tris.Count, _lines.Count);
    }

    private void Draw(CommandBuffer commands, VkPipeline pipeline, in Matrix4x4 viewProj, float pull, float alpha, int first, int count)
    {
        if (count == 0)
            return;

        var push = new Push { ViewProj = viewProj, Params = new Vector4(pull, alpha, 0f, 0f) };
        commands.BindPipeline(VkPipelineBindPoint.Graphics, pipeline);
        commands.PushConstants(_layout, VkShaderStageFlags.VertexBit, 0, new ReadOnlySpan<byte>(&push, PushBytes));
        commands.Draw(count, 1, first, 0);
    }

    private VkPipeline CreatePipeline(VkShaderModule vert, VkShaderModule frag, VkRenderPass pass, VkPrimitiveTopology topology, VkCompareOp compare)
    {
        var entry = "main"u8;
        fixed (byte* name = entry)
        {
            var stages = stackalloc VkPipelineShaderStageCreateInfo[]
            {
                new() { Stage = VkShaderStageFlags.VertexBit, Module = vert, Name = name },
                new() { Stage = VkShaderStageFlags.FragmentBit, Module = frag, Name = name },
            };
            var binding = new VkVertexInputBindingDescription { Binding = 0, Stride = (ByteSize32)sizeof(Vertex), InputRate = VkVertexInputRate.Vertex };
            var attributes = stackalloc VkVertexInputAttributeDescription[]
            {
                new() { Location = 0, Binding = 0, Format = VkFormat.R32G32B32SFloat, Offset = (ByteSize32)0 },
                new() { Location = 1, Binding = 0, Format = VkFormat.R8G8B8A8UNorm, Offset = (ByteSize32)12 },
            };
            var vertexInput = new VkPipelineVertexInputStateCreateInfo
            {
                VertexBindingDescriptionCount = 1,
                VertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 2,
                VertexAttributeDescriptions = attributes,
            };
            var inputAssembly = new VkPipelineInputAssemblyStateCreateInfo { Topology = topology };
            var viewport = new VkPipelineViewportStateCreateInfo { ViewportCount = 1, ScissorCount = 1 };
            var raster = new VkPipelineRasterizationStateCreateInfo { PolygonMode = VkPolygonMode.Fill, CullMode = VkCullModeFlags.None, LineWidth = 1f };
            var multisample = new VkPipelineMultisampleStateCreateInfo { RasterizationSamples = VkSampleCountFlags._1Bit };
            var depth = new VkPipelineDepthStencilStateCreateInfo { DepthTestEnable = true, DepthWriteEnable = false, DepthCompareOp = compare };
            var blendAttachment = new VkPipelineColorBlendAttachmentState
            {
                BlendEnable = true,
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

            return _renderer.Context.Pipelines.CreateGraphicsPipeline(
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
                    RenderPass = pass,
                }
            );
        }
    }

    public void Dispose()
    {
        _device.WaitIdle();
        _device.DestroyPipeline(_triVisible, null);
        _device.DestroyPipeline(_triHidden, null);
        _device.DestroyPipeline(_lineVisible, null);
        _device.DestroyPipeline(_lineHidden, null);
        _device.DestroyPipelineLayout(_layout, null);
        _buffer.Dispose();
    }
}
