using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using BrutalGame.Engine.Rendering;
using BrutalGame.Engine.Scene;

namespace BrutalGame.Terrain.Runtime.Rendering;

/// <summary>
/// One body's record for a frame (body.glsl <c>Body</c>, std430). Camera frame = the camera's frame axes, camera-relative;
/// body axes = the body's surface frame, centred on the body. Filled by the caller (<see cref="DetailLadder"/>).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct BodyDraw
{
    /// <summary>xyz centre − camera (camera frame), w bounding radius.</summary>
    public Vector4 Centre;

    /// <summary>Camera frame axes to body axes.</summary>
    public Quaternion Rotation;

    /// <summary>xyz toward the star (body axes), w intensity.</summary>
    public Vector4 Sun;

    /// <summary>x kind (0 rocky, 1 star), y fullscreen, z ground alpha, w depth scale (above 1 draws nearer, below 1 farther).</summary>
    public Vector4 Shape;

    /// <summary>x datum radius, y base height (sea or 0) about it, z atmosphere top radius (0 none), w scale height.</summary>
    public Vector4 Radii;

    /// <summary>x height at R16 0, y height span, z relief 0..1, w steepest baked slope.</summary>
    public Vector4 Relief;

    /// <summary>x texel angle at level 0 (rad), y coarsest level, z atmosphere rim alpha.</summary>
    public Vector4 Lod;

    /// <summary>rgb point of light (display), w point weight.</summary>
    public Vector4 Colour;

    /// <summary>rgb star emission (linear HDR), w the star's angular radius seen from the body.</summary>
    public Vector4 Emission;

    /// <summary>xyz occluder centre (body axes, from the body's centre), w radius; 0 for none.</summary>
    public Vector4 Occluder0;

    public Vector4 Occluder1;
    public Vector4 Reserved;
}

/// <summary>
/// Draws celestial bodies on the detail ladder (planets.md "Detail ladder" stages 1-3): every body's point of light in
/// one instanced draw, then one draw per disc or impostor (body.frag), each sampling its <see cref="ImpostorTexture"/>
/// through a descriptor set from <see cref="AllocateSet"/>, pointed at it by <see cref="WriteSet"/> (sets are never freed:
/// the caller reuses them once no frame in flight reads them). Records live in a per-frame storage buffer, so the shared
/// instance store (sized for terrain) is not used. Main thread only; inside the opaque pass after terrain.
/// </summary>
public sealed unsafe class BodyRenderer : IDisposable
{
    public const int MaxBodies = 64;

    public const int MaxImpostorSets = 32;
    private const int UniformAlignment = 256;
    private const int BodyVertices = 6;

    private readonly Device _device;
    private readonly PipelineCache _pipelines;
    private readonly GpuBuffer _uniforms;
    private readonly GpuBuffer _bodies;
    private readonly VkDescriptorSetLayout _frameLayout;
    private readonly VkDescriptorSetLayout _impostorLayout;
    private readonly VkDescriptorPool _framePool;
    private readonly VkDescriptorPool _impostorPool;
    private readonly VkDescriptorSet[] _sets = new VkDescriptorSet[Renderer.FramesInFlight];
    private readonly VkSampler _sampler;
    private readonly VkPipelineLayout _layout;
    private readonly VkPipeline _bodyPipeline;
    private readonly VkPipeline _pointPipeline;

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameUniforms
    {
        public Matrix4x4 ViewProj;
        public Vector4 Right;
        public Vector4 Up;
        public Vector4 Forward;
        public Vector4 Viewport;
    }

    public BodyRenderer(Renderer renderer)
    {
        var context = renderer.Context;
        _device = context.Device;
        _pipelines = context.Pipelines;
        _uniforms = new GpuBuffer(context, UniformStride * Renderer.FramesInFlight, VkBufferUsageFlags.UniformBufferBit, true);
        _bodies = new GpuBuffer(context, BodyStride * Renderer.FramesInFlight, VkBufferUsageFlags.StorageBufferBit, true);
        _frameLayout = CreateFrameLayout();
        _impostorLayout = CreateImpostorLayout();
        _framePool = CreatePool(VkDescriptorPoolCreateFlags.None, Renderer.FramesInFlight,
            [new VkDescriptorPoolSize { Type = VkDescriptorType.UniformBuffer, DescriptorCount = Renderer.FramesInFlight },
             new VkDescriptorPoolSize { Type = VkDescriptorType.StorageBuffer, DescriptorCount = Renderer.FramesInFlight }]);
        _impostorPool = CreatePool(VkDescriptorPoolCreateFlags.None, MaxImpostorSets,
            [new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = 2 * MaxImpostorSets }]);
        CreateFrameSets();
        _sampler = _device.CreateSampler(
            new VkSamplerCreateInfo
            {
                MagFilter = VkFilter.Linear,
                MinFilter = VkFilter.Linear,
                MipmapMode = VkSamplerMipmapMode.Linear,
                AddressModeU = VkSamplerAddressMode.ClampToEdge,
                AddressModeV = VkSamplerAddressMode.ClampToEdge,
                AddressModeW = VkSamplerAddressMode.ClampToEdge,
                MaxLod = 1000f,
            },
            null
        );

        Span<VkPushConstantRange> push =
        [
            new VkPushConstantRange { StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, Size = (ByteSize32)sizeof(uint) },
        ];
        _layout = _device.CreatePipelineLayout([_frameLayout, _impostorLayout], push, null);
        _bodyPipeline = CreatePipeline(renderer.RenderPass, "body", additive: false);
        _pointPipeline = CreatePipeline(renderer.RenderPass, "body_point", additive: true);
    }

    private static int UniformStride => (sizeof(FrameUniforms) + UniformAlignment - 1) / UniformAlignment * UniformAlignment;

    private static int BodyStride => sizeof(BodyDraw) * MaxBodies;

    /// <summary>A descriptor set for one impostor (at most <see cref="MaxImpostorSets"/>); <see cref="WriteSet"/> points it at a texture.</summary>
    public VkDescriptorSet AllocateSet()
    {
        var layout = _impostorLayout;
        Span<VkDescriptorSet> set = stackalloc VkDescriptorSet[1];
        _device.AllocateDescriptorSets(
            new VkDescriptorSetAllocateInfo { DescriptorPool = _impostorPool, DescriptorSetCount = 1, SetLayouts = &layout },
            set
        );

        return set[0];
    }

    /// <summary>Points <paramref name="set"/> at <paramref name="texture"/>'s cubes. No frame in flight may be using the set.</summary>
    public void WriteSet(VkDescriptorSet set, ImpostorTexture texture)
    {
        var images = stackalloc VkDescriptorImageInfo[]
        {
            new() { Sampler = _sampler, ImageView = texture.HeightView, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal },
            new() { Sampler = _sampler, ImageView = texture.AlbedoView, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal },
        };

        _device.UpdateDescriptorSets(
            [
                new VkWriteDescriptorSet { DstSet = set, DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = images },
                new VkWriteDescriptorSet { DstSet = set, DstBinding = 1, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = images + 1 },
            ],
            []
        );
    }

    /// <summary>Writes the frame's camera; returns its body records (<see cref="MaxBodies"/>) to fill before drawing.</summary>
    public Span<BodyDraw> Begin(int frameIndex, FlyCamera camera, float aspect, VkExtent2D extent)
    {
        var f = 1f / MathF.Tan(camera.FovY * 0.5f);
        var uniforms = (FrameUniforms*)((byte*)_uniforms.Mapped + frameIndex * UniformStride);
        uniforms->ViewProj = camera.ViewProjection(aspect);
        uniforms->Right = new Vector4(ToFloat(camera.Right) * (aspect / f), camera.Near);
        uniforms->Up = new Vector4(ToFloat(camera.Rotation * Brutal.Numerics.double3.UnitY) / f, 0f);
        uniforms->Forward = new Vector4(ToFloat(camera.Forward), 2f / (f * extent.Height));
        uniforms->Viewport = new Vector4(extent.Width, extent.Height, 1f / extent.Width, 1f / extent.Height);
        return new Span<BodyDraw>((byte*)_bodies.Mapped + frameIndex * BodyStride, MaxBodies);
    }

    /// <summary>Every body with a point weight, as points of light: one instanced draw over the first <paramref name="count"/> records.</summary>
    public void DrawPoints(CommandBuffer commands, int frameIndex, VkExtent2D extent, int count)
    {
        if (count == 0)
            return;

        Bind(commands, frameIndex, extent, _pointPipeline);
        commands.Draw(BodyVertices, count, 0, 0);
    }

    /// <summary>Binds the body pipeline; then <see cref="DrawBody"/> per disc or impostor.</summary>
    public void BeginBodies(CommandBuffer commands, int frameIndex, VkExtent2D extent) => Bind(commands, frameIndex, extent, _bodyPipeline);

    public void DrawBody(CommandBuffer commands, int index, VkDescriptorSet impostor)
    {
        var body = (uint)index;
        commands.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 1, [impostor], []);
        commands.PushConstants(_layout, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, 0, new ReadOnlySpan<byte>(&body, sizeof(uint)));
        commands.Draw(BodyVertices, 1, 0, 0);
    }

    private void Bind(CommandBuffer commands, int frameIndex, VkExtent2D extent, VkPipeline pipeline)
    {
        commands.BindPipeline(VkPipelineBindPoint.Graphics, pipeline);
        commands.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 0, [_sets[frameIndex]], []);
        commands.SetViewport(0, [new VkViewport { Width = extent.Width, Height = extent.Height, MaxDepth = 1f }]);
        commands.SetScissor(0, [new VkRect2D { Extent = extent }]);
    }

    private static Vector3 ToFloat(Brutal.Numerics.double3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    private VkDescriptorSetLayout CreateFrameLayout()
    {
        var bindings = stackalloc VkDescriptorSetLayoutBinding[]
        {
            new() { Binding = 0, DescriptorType = VkDescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit },
            new() { Binding = 1, DescriptorType = VkDescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit },
        };

        return _device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = 2, Bindings = bindings }, null);
    }

    private VkDescriptorSetLayout CreateImpostorLayout()
    {
        var bindings = stackalloc VkDescriptorSetLayoutBinding[]
        {
            new() { Binding = 0, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit },
            new() { Binding = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = VkShaderStageFlags.FragmentBit },
        };

        return _device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = 2, Bindings = bindings }, null);
    }

    private VkDescriptorPool CreatePool(VkDescriptorPoolCreateFlags flags, int maxSets, ReadOnlySpan<VkDescriptorPoolSize> sizes)
    {
        fixed (VkDescriptorPoolSize* s = sizes)
        {
            return _device.CreateDescriptorPool(
                new VkDescriptorPoolCreateInfo { Flags = flags, MaxSets = maxSets, PoolSizeCount = sizes.Length, PoolSizes = s },
                null
            );
        }
    }

    private void CreateFrameSets()
    {
        var layouts = stackalloc VkDescriptorSetLayout[Renderer.FramesInFlight];

        for (var i = 0; i < Renderer.FramesInFlight; i++)
            layouts[i] = _frameLayout;

        _device.AllocateDescriptorSets(
            new VkDescriptorSetAllocateInfo { DescriptorPool = _framePool, DescriptorSetCount = Renderer.FramesInFlight, SetLayouts = layouts },
            _sets
        );

        for (var i = 0; i < Renderer.FramesInFlight; i++)
        {
            var uniforms = new VkDescriptorBufferInfo { Buffer = _uniforms.Handle, Offset = (ByteSize64)(i * UniformStride), Range = (ByteSize64)sizeof(FrameUniforms) };
            var bodies = new VkDescriptorBufferInfo { Buffer = _bodies.Handle, Offset = (ByteSize64)(i * BodyStride), Range = (ByteSize64)BodyStride };

            _device.UpdateDescriptorSets(
                [
                    new VkWriteDescriptorSet { DstSet = _sets[i], DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.UniformBuffer, BufferInfo = &uniforms },
                    new VkWriteDescriptorSet { DstSet = _sets[i], DstBinding = 1, DescriptorCount = 1, DescriptorType = VkDescriptorType.StorageBuffer, BufferInfo = &bodies },
                ],
                []
            );
        }
    }

    // Bodies: premultiplied alpha, depth written from the fragment shader (GEQUAL, so the rim's depth 0 passes over sky).
    // Points: added, depth-tested, not written. No culling: quads face the camera.
    private VkPipeline CreatePipeline(VkRenderPass renderPass, string shader, bool additive)
    {
        var vert = ShaderLoader.Load(_device, shader + ".vert", ShaderKind.VertexShader);
        var frag = ShaderLoader.Load(_device, shader + ".frag", ShaderKind.FragmentShader);

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

                var vertexInput = new VkPipelineVertexInputStateCreateInfo();
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
                    DepthWriteEnable = !additive,
                    DepthCompareOp = VkCompareOp.GreaterOrEqual,
                };
                var blendAttachment = new VkPipelineColorBlendAttachmentState
                {
                    BlendEnable = true,
                    SrcColorBlendFactor = VkBlendFactor.One,
                    DstColorBlendFactor = additive ? VkBlendFactor.One : VkBlendFactor.OneMinusSrcAlpha,
                    ColorBlendOp = VkBlendOp.Add,
                    SrcAlphaBlendFactor = VkBlendFactor.One,
                    DstAlphaBlendFactor = additive ? VkBlendFactor.One : VkBlendFactor.OneMinusSrcAlpha,
                    AlphaBlendOp = VkBlendOp.Add,
                    ColorWriteMask = VkColorComponentFlags.RBit | VkColorComponentFlags.GBit | VkColorComponentFlags.BBit | VkColorComponentFlags.ABit,
                };
                var blend = new VkPipelineColorBlendStateCreateInfo { AttachmentCount = 1, Attachments = &blendAttachment };
                var dynamicStates = stackalloc VkDynamicState[] { VkDynamicState.Viewport, VkDynamicState.Scissor };
                var dynamic = new VkPipelineDynamicStateCreateInfo { DynamicStateCount = 2, DynamicStates = dynamicStates };

                return _pipelines.CreateGraphicsPipeline(
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

    /// <summary>Waits for the device, then destroys everything. Impostor textures and their sets are the caller's.</summary>
    public void Dispose()
    {
        _device.WaitIdle();
        _device.DestroyPipeline(_bodyPipeline, null);
        _device.DestroyPipeline(_pointPipeline, null);
        _device.DestroyPipelineLayout(_layout, null);
        _device.DestroySampler(_sampler, null);
        _device.DestroyDescriptorPool(_framePool, null);
        _device.DestroyDescriptorPool(_impostorPool, null);
        _device.DestroyDescriptorSetLayout(_frameLayout, null);
        _device.DestroyDescriptorSetLayout(_impostorLayout, null);
        _bodies.Dispose();
        _uniforms.Dispose();
    }
}
