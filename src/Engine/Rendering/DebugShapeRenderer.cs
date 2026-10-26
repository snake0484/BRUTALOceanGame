using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace BrutalGame.Engine.Rendering;

/// <summary>
/// Solid lit boxes and spheres for debugging (physics bodies etc.). Geometry comes from gl_VertexIndex,
/// instances from a per-frame host-visible storage buffer. Main thread only.
/// Per frame: Clear, Add*, then Draw inside the main pass.
/// </summary>
public sealed unsafe class DebugShapeRenderer : IDisposable
{
    public const int MaxShapes = 4096;

    private const int BoxVertices = 36;
    private const int SphereSegments = 16;
    private const int SphereRings = 12;
    private const int SphereVertices = SphereSegments * SphereRings * 6;
    private const int UniformAlignment = 256;
    private const int PushConstantSize = 4;
    private const uint KindBox = 0;
    private const uint KindSphere = 1;

    private readonly Device _device;
    private readonly GpuBuffer _uniforms;
    private readonly GpuBuffer _instances;
    private readonly VkDescriptorSetLayout _setLayout;
    private readonly VkDescriptorPool _pool;
    private readonly VkDescriptorSet[] _sets = new VkDescriptorSet[Renderer.FramesInFlight];
    private readonly VkPipelineLayout _layout;
    private readonly VkPipeline _pipeline;

    // Boxes fill from the front, spheres from the back, so both share MaxShapes and draw as contiguous ranges.
    private readonly Instance[] _staged = new Instance[MaxShapes];
    private int _boxCount;
    private int _sphereCount;

    [StructLayout(LayoutKind.Sequential)]
    private struct Instance
    {
        public Matrix4x4 Model;
        public Vector4 Colour;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameUniforms
    {
        public Matrix4x4 ViewProj;
        public Vector4 CameraPos;
        public Vector4 SunDir;
        public Vector4 SkyZenith;
        public Vector4 SkyHorizon;
    }

    public DebugShapeRenderer(Renderer renderer)
    {
        var context = renderer.Context;
        _device = context.Device;

        _uniforms = new GpuBuffer(context, UniformStride * Renderer.FramesInFlight, VkBufferUsageFlags.UniformBufferBit, true);
        _instances = new GpuBuffer(context, InstanceFrameBytes * Renderer.FramesInFlight, VkBufferUsageFlags.StorageBufferBit, true);

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

    public int BoxCount => _boxCount;

    public int SphereCount => _sphereCount;

    public int Count => _boxCount + _sphereCount;

    private static int UniformStride => (sizeof(FrameUniforms) + UniformAlignment - 1) / UniformAlignment * UniformAlignment;

    // 4096 * 80 B = 320 KiB, a multiple of 256 so per-frame offsets meet storage buffer alignment.
    private static int InstanceFrameBytes => MaxShapes * sizeof(Instance);

    public void Clear()
    {
        _boxCount = 0;
        _sphereCount = 0;
    }

    /// <summary>Drops the shape when MaxShapes is reached. Colour is linear albedo.</summary>
    public void AddBox(Vector3 position, Quaternion rotation, Vector3 halfExtents, Vector3 colour)
    {
        if (Count >= MaxShapes)
            return;

        ref var instance = ref _staged[_boxCount++];
        instance.Model = Matrix4x4.CreateScale(halfExtents) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
        instance.Colour = new Vector4(colour, 1f);
    }

    /// <summary>Drops the shape when MaxShapes is reached. Colour is linear albedo.</summary>
    public void AddSphere(Vector3 position, Quaternion rotation, float radius, Vector3 colour)
    {
        if (Count >= MaxShapes)
            return;

        ref var instance = ref _staged[MaxShapes - 1 - _sphereCount++];
        instance.Model = Matrix4x4.CreateScale(radius) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
        instance.Colour = new Vector4(colour, 1f);
    }

    /// <summary>Inside the main pass: uploads this frame's shapes and issues one instanced draw per shape kind.</summary>
    public void Draw(CommandBuffer commands, int frameIndex, VkExtent2D extent, in Matrix4x4 viewProj, Vector3 cameraPos, in SceneLighting lighting)
    {
        if (Count == 0)
            return;

        var uniforms = (FrameUniforms*)((byte*)_uniforms.Mapped + frameIndex * UniformStride);
        uniforms->ViewProj = viewProj;
        uniforms->CameraPos = new Vector4(cameraPos, 0f);
        uniforms->SunDir = new Vector4(lighting.SunDirection, lighting.SunIntensity);
        uniforms->SkyZenith = new Vector4(lighting.SkyZenith, 0f);
        uniforms->SkyHorizon = new Vector4(lighting.SkyHorizon, lighting.FogDensity);

        var mapped = new Span<Instance>((byte*)_instances.Mapped + frameIndex * InstanceFrameBytes, MaxShapes);
        var firstSphere = MaxShapes - _sphereCount;
        _staged.AsSpan(0, _boxCount).CopyTo(mapped);
        _staged.AsSpan(firstSphere, _sphereCount).CopyTo(mapped[firstSphere..]);

        commands.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline);
        commands.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 0, [_sets[frameIndex]], []);
        commands.SetViewport(0, [new VkViewport { Width = extent.Width, Height = extent.Height, MaxDepth = 1f }]);
        commands.SetScissor(0, [new VkRect2D { Extent = extent }]);

        var kind = KindBox;

        if (_boxCount > 0)
        {
            commands.PushConstants(_layout, VkShaderStageFlags.VertexBit, 0, new ReadOnlySpan<byte>(&kind, PushConstantSize));
            commands.Draw(BoxVertices, _boxCount, 0, 0);
        }

        if (_sphereCount > 0)
        {
            kind = KindSphere;
            commands.PushConstants(_layout, VkShaderStageFlags.VertexBit, 0, new ReadOnlySpan<byte>(&kind, PushConstantSize));
            commands.Draw(SphereVertices, _sphereCount, 0, firstSphere);
        }
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
            new()
            {
                Binding = 1,
                DescriptorType = VkDescriptorType.StorageBuffer,
                DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.VertexBit,
            },
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
            var uniformInfo = new VkDescriptorBufferInfo
            {
                Buffer = _uniforms.Handle,
                Offset = (ByteSize64)(i * UniformStride),
                Range = (ByteSize64)sizeof(FrameUniforms),
            };
            var instanceInfo = new VkDescriptorBufferInfo
            {
                Buffer = _instances.Handle,
                Offset = (ByteSize64)(i * InstanceFrameBytes),
                Range = (ByteSize64)InstanceFrameBytes,
            };

            _device.UpdateDescriptorSets(
                [
                    new VkWriteDescriptorSet
                    {
                        DstSet = _sets[i],
                        DstBinding = 0,
                        DescriptorCount = 1,
                        DescriptorType = VkDescriptorType.UniformBuffer,
                        BufferInfo = &uniformInfo,
                    },
                    new VkWriteDescriptorSet
                    {
                        DstSet = _sets[i],
                        DstBinding = 1,
                        DescriptorCount = 1,
                        DescriptorType = VkDescriptorType.StorageBuffer,
                        BufferInfo = &instanceInfo,
                    },
                ],
                []
            );
        }
    }

    // Shapes wind CCW seen from outside (cross(b - a, c - a) points outward). The projection's -f on Y flips clip Y,
    // which cancels Vulkan's y-down framebuffer, so outside CCW stays CCW: cull back, front = CCW (same as terrain).
    // Negative scales would mirror the winding; not supported.
    private VkPipeline CreatePipeline(VkRenderPass renderPass)
    {
        var vert = ShaderLoader.Load(_device, "debug_shape.vert", ShaderKind.VertexShader);
        var frag = ShaderLoader.Load(_device, "debug_shape.frag", ShaderKind.FragmentShader);

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

        _instances.Dispose();
        _uniforms.Dispose();
    }
}
