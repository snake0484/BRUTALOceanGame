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
/// </summary>
public sealed unsafe class HullRenderer : IDisposable
{
    public const int PaletteSize = 32;
    private const int UniformAlignment = 256;
    private const int PushConstantSize = 16;
    private const int VertexStride = PackedTerrainVertex.SizeInBytes;

    private readonly Renderer _renderer;
    private readonly Device _device;
    private readonly GpuBuffer _uniforms;
    private readonly VkDescriptorSetLayout _setLayout;
    private readonly VkDescriptorPool _pool;
    private readonly VkDescriptorSet[] _sets = new VkDescriptorSet[Renderer.FramesInFlight];
    private readonly VkPipelineLayout _layout;
    private readonly VkPipeline _pipeline;
    private readonly List<Draw> _draws = new();
    private readonly Vector4[] _palette = new Vector4[PaletteSize];
    private GpuBuffer? _vertices;
    private GpuBuffer? _indices;

    private struct Draw
    {
        public Vector3d Origin;
        public int FirstIndex;
        public int IndexCount;
        public int VertexOffset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameUniforms
    {
        public Matrix4x4 ViewProj;
        public Vector4 SunDir;
        public Vector4 SkyZenith;
        public Vector4 SkyHorizon;
        public Palette Colours;
    }

    [InlineArray(PaletteSize)]
    private struct Palette
    {
        private Vector4 _element;
    }

    public HullRenderer(Renderer renderer)
    {
        _renderer = renderer;
        var context = renderer.Context;
        _device = context.Device;
        _uniforms = new GpuBuffer(context, UniformStride * Renderer.FramesInFlight, VkBufferUsageFlags.UniformBufferBit, true);
        _setLayout = CreateSetLayout();
        _pool = CreatePool();
        CreateSets();

        Span<VkPushConstantRange> push =
        [
            new VkPushConstantRange { StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, Size = (ByteSize32)PushConstantSize },
        ];
        _layout = _device.CreatePipelineLayout([_setLayout], push, null);
        _pipeline = CreatePipeline(context.Pipelines, renderer.RenderPass);
        _palette.AsSpan().Fill(new Vector4(0.6f, 0.6f, 0.6f, 1f));
    }

    public int BrickCount => _draws.Count;

    public long VertexBytes => _vertices?.Size ?? 0;

    public long IndexBytes => _indices?.Size ?? 0;

    private static int UniformStride => (sizeof(FrameUniforms) + UniformAlignment - 1) / UniformAlignment * UniformAlignment;

    /// <summary>Linear albedo per plan material index; entries past <see cref="PaletteSize"/> are ignored.</summary>
    public void SetPalette(ReadOnlySpan<Vector3> colours)
    {
        for (var i = 0; i < Math.Min(colours.Length, PaletteSize); i++)
            _palette[i] = new Vector4(colours[i], 1f);
    }

    /// <summary>Replaces the drawn bricks. Waits for the device: call on commit only.</summary>
    public void Upload(IReadOnlyList<HullBrick> bricks)
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

        foreach (var brick in bricks)
        {
            if (brick.IndexCount == 0)
                continue;

            brick.VertexSpan.CopyTo(vertices[v..]);
            brick.IndexSpan.CopyTo(indices[i..]);
            _draws.Add(new Draw { Origin = brick.Origin, FirstIndex = i, IndexCount = brick.IndexCount, VertexOffset = v });
            v += brick.VertexCount;
            i += brick.IndexCount;
        }
    }

    /// <summary>Inside the opaque pass. <paramref name="viewProj"/> has no translation; <paramref name="camera"/> is in ship metres.</summary>
    public void Record(CommandBuffer commands, int frameIndex, VkExtent2D extent, in Matrix4x4 viewProj, double3 camera, in SceneLighting lighting)
    {
        if (_draws.Count == 0)
            return;

        var uniforms = (FrameUniforms*)((byte*)_uniforms.Mapped + frameIndex * UniformStride);
        uniforms->ViewProj = viewProj;
        uniforms->SunDir = new Vector4(lighting.SunDirection, lighting.SunIntensity);
        uniforms->SkyZenith = new Vector4(lighting.SkyZenith, 0f);
        uniforms->SkyHorizon = new Vector4(lighting.SkyHorizon, 0f);

        for (var p = 0; p < PaletteSize; p++)
            uniforms->Colours[p] = _palette[p];

        commands.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline);
        commands.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 0, [_sets[frameIndex]], []);
        commands.SetViewport(0, [new VkViewport { Width = extent.Width, Height = extent.Height, MaxDepth = 1f }]);
        commands.SetScissor(0, [new VkRect2D { Extent = extent }]);
        commands.BindVertexBuffers(0, [_vertices!.Handle], [(ByteSize64)0]);
        commands.BindIndexBuffer(_indices!.Handle, (ByteSize64)0, VkIndexType.UInt32);

        const float scale = HullBins.CellMetres / PackedTerrainVertex.StepsPerVoxel;

        foreach (var draw in _draws)
        {
            var push = new Vector4((float)(draw.Origin.X - camera.X), (float)(draw.Origin.Y - camera.Y), (float)(draw.Origin.Z - camera.Z), scale);
            commands.PushConstants(_layout, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, 0, new ReadOnlySpan<byte>(&push, PushConstantSize));
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

    private VkDescriptorSetLayout CreateSetLayout()
    {
        var binding = new VkDescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = VkDescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
        };

        return _device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = 1, Bindings = &binding }, null);
    }

    private VkDescriptorPool CreatePool()
    {
        var size = new VkDescriptorPoolSize { Type = VkDescriptorType.UniformBuffer, DescriptorCount = Renderer.FramesInFlight };
        return _device.CreateDescriptorPool(new VkDescriptorPoolCreateInfo { MaxSets = Renderer.FramesInFlight, PoolSizeCount = 1, PoolSizes = &size }, null);
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

        for (var i = 0; i < Renderer.FramesInFlight; i++)
        {
            var info = new VkDescriptorBufferInfo
            {
                Buffer = _uniforms.Handle,
                Offset = (ByteSize64)(i * UniformStride),
                Range = (ByteSize64)sizeof(FrameUniforms),
            };

            _device.UpdateDescriptorSets(
                [
                    new VkWriteDescriptorSet
                    {
                        DstSet = _sets[i],
                        DstBinding = 0,
                        DescriptorCount = 1,
                        DescriptorType = VkDescriptorType.UniformBuffer,
                        BufferInfo = &info,
                    },
                ],
                []
            );
        }
    }

    private VkPipeline CreatePipeline(PipelineCache pipelines, VkRenderPass renderPass)
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
                    CullMode = VkCullModeFlags.BackBit,
                    FrontFace = VkFrontFace.CounterClockwise,
                    LineWidth = 1f,
                };
                var multisample = new VkPipelineMultisampleStateCreateInfo { RasterizationSamples = VkSampleCountFlags._1Bit };
                var depth = new VkPipelineDepthStencilStateCreateInfo
                {
                    DepthTestEnable = true,
                    DepthWriteEnable = true,
                    DepthCompareOp = VkCompareOp.Greater,
                };
                var blendAttachment = new VkPipelineColorBlendAttachmentState
                {
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
        _device.DestroyPipelineLayout(_layout, null);
        _device.DestroyDescriptorPool(_pool, null);
        _device.DestroyDescriptorSetLayout(_setLayout, null);
        _vertices?.Dispose();
        _indices?.Dispose();
        _uniforms.Dispose();
    }
}
