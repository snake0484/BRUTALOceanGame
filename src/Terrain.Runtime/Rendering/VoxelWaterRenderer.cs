using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using BrutalGame.Engine.Rendering;
using BrutalGame.Terrain.Water;

namespace BrutalGame.Terrain.Runtime.Rendering;

/// <summary>
/// Draws voxel water surface meshes, re-streamed every frame into a host-visible per-frame ring.
/// Main thread only. Per frame: Renderer.BeginFrame, BeginFrame, Add (per mesh), Draw (in the main pass, after opaque + ocean).
/// </summary>
public sealed unsafe class VoxelWaterRenderer : IDisposable
{
    public const int VertexStride = 28;
    public const int RegionVertexBytes = 8 << 20;
    public const int RegionIndexBytes = 8 << 20;
    public const int MaxDraws = 1024;

    private const int RegionVertices = RegionVertexBytes / VertexStride;
    private const int RegionIndices = RegionIndexBytes / sizeof(uint);
    private const int UniformAlignment = 256;
    private const int PushConstantSize = 16;
    private const float CullMargin = 1f;

    private readonly Device _device;
    private readonly GpuBuffer _vertices;
    private readonly GpuBuffer _indices;
    private readonly GpuBuffer _uniforms;
    private readonly VkDescriptorSetLayout _setLayout;
    private readonly VkDescriptorPool _pool;

    /// <summary>Where rain can't reach (rain_cover.glsl, binding 1): no rings on water under cover.</summary>
    private readonly RainCover _rainCover;
    private readonly VkDescriptorSet[] _sets = new VkDescriptorSet[Renderer.FramesInFlight];
    private readonly VkPipelineLayout _layout;
    private readonly VkPipeline _pipeline;
    private readonly DrawEntry[] _draws = new DrawEntry[MaxDraws];

    private int _frameIndex = -1;
    private int _drawCount;
    private int _vertexCursor;
    private int _indexCursor;

    private struct DrawEntry
    {
        public Vector3 Origin;
        public float Size;
        public int FirstIndex;
        public int VertexOffset;
        public int IndexCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameUniforms
    {
        public Matrix4x4 ViewProj;
        public Vector4 CameraPos;
        public Vector4 SunDir;
        public Vector4 SkyZenith;
        public Vector4 SkyHorizon;
        public Vector4 Rain;
    }

    public VoxelWaterRenderer(Renderer renderer, RainCover rainCover)
    {
        _rainCover = rainCover;

        if (sizeof(WaterVertex) != VertexStride)
            throw new InvalidOperationException($"WaterVertex is {sizeof(WaterVertex)} bytes, expected {VertexStride}.");

        var context = renderer.Context;
        _device = context.Device;

        _vertices = new GpuBuffer(context, RegionVertices * VertexStride * Renderer.FramesInFlight, VkBufferUsageFlags.VertexBufferBit, true);
        _indices = new GpuBuffer(context, RegionIndices * sizeof(uint) * Renderer.FramesInFlight, VkBufferUsageFlags.IndexBufferBit, true);
        _uniforms = new GpuBuffer(context, UniformStride * Renderer.FramesInFlight, VkBufferUsageFlags.UniformBufferBit, true);

        _setLayout = CreateSetLayout();
        _pool = CreatePool();
        CreateSets();

        Span<VkPushConstantRange> push =
        [
            new VkPushConstantRange { StageFlags = VkShaderStageFlags.VertexBit, Size = (ByteSize32)PushConstantSize },
        ];
        _layout = _device.CreatePipelineLayout([_setLayout], push, null);
        _pipeline = CreatePipeline(renderer.RenderPass);
    }

    public int MeshesLastFrame { get; private set; }

    public int TrianglesLastFrame { get; private set; }

    private static int UniformStride => (sizeof(FrameUniforms) + UniformAlignment - 1) / UniformAlignment * UniformAlignment;

    /// <summary>Resets this frame's ring. Call after Renderer.BeginFrame so the frame's fence has been waited on.</summary>
    public void BeginFrame(int frameIndex)
    {
        _frameIndex = frameIndex;
        _drawCount = 0;
        _vertexCursor = 0;
        _indexCursor = 0;
    }

    /// <summary>Copies the mesh into this frame's ring. Returns false if the ring or draw list is full.</summary>
    public bool Add(ChunkCoord coord, WaterMesh mesh)
    {
        var vertexCount = mesh.VertexCount;
        var indexCount = mesh.IndexCount;

        if (_frameIndex < 0)
            return false;

        if (vertexCount == 0 || indexCount == 0)
            return true;

        if (_drawCount == MaxDraws || _vertexCursor + vertexCount > RegionVertices || _indexCursor + indexCount > RegionIndices)
            return false;

        var vertexBase = _frameIndex * RegionVertices + _vertexCursor;
        var indexBase = _frameIndex * RegionIndices + _indexCursor;

        var vertexDst = new Span<byte>((byte*)_vertices.Mapped + (long)vertexBase * VertexStride, vertexCount * VertexStride);
        var indexDst = new Span<uint>((uint*)_indices.Mapped + indexBase, indexCount);
        MemoryMarshal.AsBytes(mesh.Vertices.AsSpan(0, vertexCount)).CopyTo(vertexDst);
        mesh.Indices.AsSpan(0, indexCount).CopyTo(indexDst);

        _draws[_drawCount++] = new DrawEntry
        {
            Origin = coord.WorldOrigin,
            Size = coord.SizeMetres,
            FirstIndex = indexBase,
            VertexOffset = vertexBase,
            IndexCount = indexCount,
        };
        _vertexCursor += vertexCount;
        _indexCursor += indexCount;
        return true;
    }

    /// <summary>Inside the main pass, after opaque geometry and the ocean: frustum-culls and draws this frame's meshes.</summary>
    /// <param name="rain">Rain intensity 0..1 and its looped time (s), as the ocean gets them: rings on the water (rain_ripples.glsl).</param>
    public void Draw(CommandBuffer commands, int frameIndex, VkExtent2D extent, in Matrix4x4 viewProj, Vector3 cameraPos, in SceneLighting lighting, float time, Vector2 rain)
    {
        MeshesLastFrame = 0;
        TrianglesLastFrame = 0;

        if (frameIndex != _frameIndex || _drawCount == 0)
            return;

        var uniforms = (FrameUniforms*)((byte*)_uniforms.Mapped + frameIndex * UniformStride);
        uniforms->ViewProj = viewProj;
        uniforms->CameraPos = new Vector4(cameraPos, time);
        uniforms->SunDir = new Vector4(lighting.SunDirection, lighting.SunIntensity);
        uniforms->SkyZenith = new Vector4(lighting.SkyZenith, 0f);
        uniforms->SkyHorizon = new Vector4(lighting.SkyHorizon, lighting.FogDensity);
        uniforms->Rain = new Vector4(rain, 0f, 0f);

        commands.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline);
        commands.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 0, [_sets[frameIndex]], []);
        commands.BindVertexBuffers(0, [_vertices.Handle], [(ByteSize64)0]);
        commands.BindIndexBuffer(_indices.Handle, (ByteSize64)0, VkIndexType.UInt32);
        commands.SetViewport(0, [new VkViewport { Width = extent.Width, Height = extent.Height, MaxDepth = 1f }]);
        commands.SetScissor(0, [new VkRect2D { Extent = extent }]);

        var planes = stackalloc Vector4[6];
        ExtractPlanes(viewProj, planes);
        var meshes = 0;
        var indices = 0;
        var push = Vector4.Zero;

        for (var i = 0; i < _drawCount; i++)
        {
            ref var draw = ref _draws[i];

            if (!Visible(planes, draw.Origin, draw.Size))
                continue;

            push = new Vector4(draw.Origin, 0f);
            commands.PushConstants(_layout, VkShaderStageFlags.VertexBit, 0, new ReadOnlySpan<byte>(&push, PushConstantSize));
            commands.DrawIndexed(draw.IndexCount, 1, draw.FirstIndex, draw.VertexOffset, 0);
            meshes++;
            indices += draw.IndexCount;
        }

        MeshesLastFrame = meshes;
        TrianglesLastFrame = indices / 3;
    }

    // Row-vector convention (clip = v * M); reverse-Z near plane is z <= w.
    private static void ExtractPlanes(in Matrix4x4 m, Vector4* planes)
    {
        var cx = new Vector4(m.M11, m.M21, m.M31, m.M41);
        var cy = new Vector4(m.M12, m.M22, m.M32, m.M42);
        var cz = new Vector4(m.M13, m.M23, m.M33, m.M43);
        var cw = new Vector4(m.M14, m.M24, m.M34, m.M44);

        planes[0] = cw + cx;
        planes[1] = cw - cx;
        planes[2] = cw + cy;
        planes[3] = cw - cy;
        planes[4] = cz;
        planes[5] = cw - cz;
    }

    private static bool Visible(Vector4* planes, Vector3 origin, float size)
    {
        var min = origin - new Vector3(CullMargin);
        var max = origin + new Vector3(size + CullMargin);

        for (var i = 0; i < 6; i++)
        {
            var p = planes[i];
            var x = p.X >= 0f ? max.X : min.X;
            var y = p.Y >= 0f ? max.Y : min.Y;
            var z = p.Z >= 0f ? max.Z : min.Z;

            if (p.X * x + p.Y * y + p.Z * z + p.W < 0f)
                return false;
        }

        return true;
    }

    private VkDescriptorSetLayout CreateSetLayout()
    {
        var bindings = stackalloc VkDescriptorSetLayoutBinding[]
        {
            new()
            {
                Binding = 0,
                DescriptorType = VkDescriptorType.UniformBuffer,
                DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit,
            },
            new() { Binding = 1, DescriptorType = VkDescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit },
        };

        return _device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = 2, Bindings = bindings }, null);
    }

    private VkDescriptorPool CreatePool()
    {
        var sizes = stackalloc VkDescriptorPoolSize[]
        {
            new() { Type = VkDescriptorType.UniformBuffer, DescriptorCount = Renderer.FramesInFlight },
            new() { Type = VkDescriptorType.StorageBuffer, DescriptorCount = Renderer.FramesInFlight },
        };

        return _device.CreateDescriptorPool(
            new VkDescriptorPoolCreateInfo { MaxSets = Renderer.FramesInFlight, PoolSizeCount = 2, PoolSizes = sizes },
            null
        );
    }

    private void CreateSets()
    {
        var layouts = stackalloc VkDescriptorSetLayout[Renderer.FramesInFlight];

        for (var i = 0; i < Renderer.FramesInFlight; i++)
            layouts[i] = _setLayout;

        _device.AllocateDescriptorSets(
            new VkDescriptorSetAllocateInfo
            {
                DescriptorPool = _pool,
                DescriptorSetCount = Renderer.FramesInFlight,
                SetLayouts = layouts,
            },
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
            var cover = new VkDescriptorBufferInfo { Buffer = _rainCover.Buffer(i), Range = (ByteSize64)RainCover.Bytes };

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
                    new VkWriteDescriptorSet
                    {
                        DstSet = _sets[i],
                        DstBinding = 1,
                        DescriptorCount = 1,
                        DescriptorType = VkDescriptorType.StorageBuffer,
                        BufferInfo = &cover,
                    },
                ],
                []
            );
        }
    }

    // Alpha blended, reverse-Z depth test without writes, no culling so the surface shows from below too.
    private VkPipeline CreatePipeline(VkRenderPass renderPass)
    {
        var vert = ShaderLoader.Load(_device, "voxel_water.vert", ShaderKind.VertexShader);
        var frag = ShaderLoader.Load(_device, "voxel_water.frag", ShaderKind.FragmentShader);

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
                    new() { Location = 0, Binding = 0, Format = VkFormat.R32G32B32SFloat, Offset = (ByteSize32)0 },
                    new() { Location = 1, Binding = 0, Format = VkFormat.R32G32B32SFloat, Offset = (ByteSize32)12 },
                    new() { Location = 2, Binding = 0, Format = VkFormat.R32SFloat, Offset = (ByteSize32)24 },
                };

                var vertexInput = new VkPipelineVertexInputStateCreateInfo
                {
                    VertexBindingDescriptionCount = 1,
                    VertexBindingDescriptions = &binding,
                    VertexAttributeDescriptionCount = 3,
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
                    DepthWriteEnable = false,
                    DepthCompareOp = VkCompareOp.Greater,
                };
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

                return _device.CreateGraphicsPipeline(
                    default,
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
                    },
                    null
                );
            }
        }
        finally
        {
            _device.DestroyShaderModule(vert, null);
            _device.DestroyShaderModule(frag, null);
        }
    }

    /// <summary>Waits for the device, then destroys everything. Call before the Renderer is disposed.</summary>
    public void Dispose()
    {
        _device.WaitIdle();
        _device.DestroyPipeline(_pipeline, null);
        _device.DestroyPipelineLayout(_layout, null);
        _device.DestroyDescriptorPool(_pool, null);
        _device.DestroyDescriptorSetLayout(_setLayout, null);

        _uniforms.Dispose();
        _indices.Dispose();
        _vertices.Dispose();
    }
}
