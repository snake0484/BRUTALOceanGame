using System.Numerics;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.Numerics;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using BrutalGame.Engine.Scene;

namespace BrutalGame.Engine.Rendering;

/// <summary>
/// Placeholder bodies until the voxel planet (M2): smooth spheres ray-cast per pixel and camera-facing surface markers,
/// each placed by an instance record (review C9). Precise from metres above a 1000 km sphere out to 1e8 m: the record
/// carries centre − camera in float only as a direction; altitude and h (2R + h) come from double. Main thread only.
/// Per frame inside the opaque pass: <see cref="Begin"/>, then <see cref="DrawSphere"/> and <see cref="DrawMarker"/>.
/// </summary>
public sealed unsafe class PlanetRenderer : IDisposable
{
    private const int UniformAlignment = 256;
    private const int SphereVertices = 3;
    private const int MarkerVertices = 6;
    private const int MaxCellDecade = 4;

    private readonly Device _device;
    private readonly PipelineCache _pipelines;
    private readonly InstanceBuffer _instances;
    private readonly GpuBuffer _uniforms;
    private readonly VkDescriptorSetLayout _setLayout;
    private readonly VkDescriptorPool _pool;
    private readonly VkDescriptorSet[] _sets = new VkDescriptorSet[Renderer.FramesInFlight];
    private readonly VkPipelineLayout _layout;
    private readonly VkPipeline _pipeline;
    private double3 _camera;

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameUniforms
    {
        public Matrix4x4 ViewProj;
        public Vector4 Right;
        public Vector4 Up;
        public Vector4 Forward;
        public Vector4 Sun;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BodyConstants
    {
        public Vector4 Cell0;
        public Vector4 Cell1;
        public Vector4 Shape;
        public Vector4 Colour;
    }

    public PlanetRenderer(Renderer renderer, InstanceBuffer instances)
    {
        var context = renderer.Context;
        _device = context.Device;
        _pipelines = context.Pipelines;
        _instances = instances;
        _uniforms = new GpuBuffer(context, UniformStride * Renderer.FramesInFlight, VkBufferUsageFlags.UniformBufferBit, true);
        _setLayout = CreateSetLayout();
        _pool = CreatePool();
        CreateSets();

        Span<VkPushConstantRange> push =
        [
            new VkPushConstantRange { StageFlags = VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, Size = (ByteSize32)sizeof(BodyConstants) },
        ];
        _layout = _device.CreatePipelineLayout([_setLayout, instances.SetLayout], push, null);
        _pipeline = CreatePipeline(renderer.RenderPass);
    }

    private static int UniformStride => (sizeof(FrameUniforms) + UniformAlignment - 1) / UniformAlignment * UniformAlignment;

    /// <summary>
    /// Inside the opaque pass: writes the camera and the star direction (toward the star, camera frame axes) and binds the
    /// pipeline. <paramref name="aspect"/> must be the one the view-projection was built with.
    /// </summary>
    public void Begin(CommandBuffer commands, int frameIndex, VkExtent2D extent, FlyCamera camera, float aspect, Vector3 sun, float sunIntensity)
    {
        _camera = camera.Position;

        var f = 1f / MathF.Tan(camera.FovY * 0.5f);
        var uniforms = (FrameUniforms*)((byte*)_uniforms.Mapped + frameIndex * UniformStride);
        uniforms->ViewProj = camera.ViewProjection(aspect);
        uniforms->Right = new Vector4(ToFloat(camera.Right) * (aspect / f), camera.Near);
        uniforms->Up = new Vector4(ToFloat(camera.Rotation * double3.UnitY) / f, 0f);
        uniforms->Forward = new Vector4(ToFloat(camera.Forward), 2f / (f * extent.Height));
        uniforms->Sun = new Vector4(sun, sunIntensity);

        commands.BindPipeline(VkPipelineBindPoint.Graphics, _pipeline);
        commands.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 0, [_sets[frameIndex], _instances.Set], []);
        commands.SetViewport(0, [new VkViewport { Width = extent.Width, Height = extent.Height, MaxDepth = 1f }]);
        commands.SetScissor(0, [new VkRect2D { Extent = extent }]);
    }

    /// <summary>
    /// A sphere centred at <paramref name="centre"/> (camera frame) with body axes <paramref name="axes"/> (body to camera
    /// frame). The camera must be outside it.
    /// </summary>
    public void DrawSphere(CommandBuffer commands, double3 centre, double radius, doubleQuat axes)
    {
        var relative = centre - _camera;
        var record = _instances.Add(relative, (float)radius, ToFloat(axes));

        if (record < 0)
            return;

        var altitude = relative.Length() - radius;
        var cameraInBody = doubleQuat.Conjugate(axes) * -relative;
        var decades = Math.Log10(Math.Max(altitude, 1.0));
        var decade = Math.Clamp(Math.Floor(decades), 0.0, MaxCellDecade);
        var cell = Math.Pow(10.0, decade);
        var constants = new BodyConstants
        {
            Cell0 = new Vector4(Wrap(cameraInBody, 2.0 * cell), (float)cell),
            Cell1 = new Vector4(Wrap(cameraInBody, 20.0 * cell), (float)Math.Clamp(decades - decade, 0.0, 1.0)),
            Shape = new Vector4((float)altitude, (float)(altitude * (2.0 * radius + altitude)), 0f, 0f),
        };

        Draw(commands, constants, SphereVertices, record);
    }

    /// <summary>A marker disc of <paramref name="radius"/> at <paramref name="position"/> (camera frame), lit and a little self-lit.</summary>
    public void DrawMarker(CommandBuffer commands, double3 position, double radius, Vector3 colour)
    {
        var record = _instances.Add(position - _camera, (float)radius, Quaternion.Identity);

        if (record < 0)
            return;

        Draw(commands, new BodyConstants { Shape = new Vector4(0f, 0f, 1f, 0f), Colour = new Vector4(colour, 1f) }, MarkerVertices, record);
    }

    private void Draw(CommandBuffer commands, in BodyConstants constants, int vertices, int record)
    {
        var copy = constants;
        commands.PushConstants(_layout, VkShaderStageFlags.VertexBit | VkShaderStageFlags.FragmentBit, 0, new ReadOnlySpan<byte>(&copy, sizeof(BodyConstants)));
        commands.Draw(vertices, 1, 0, record);
    }

    private static Vector3 Wrap(double3 v, double period) =>
        new((float)(v.X - period * Math.Floor(v.X / period)), (float)(v.Y - period * Math.Floor(v.Y / period)), (float)(v.Z - period * Math.Floor(v.Z / period)));

    private static Vector3 ToFloat(double3 v) => new((float)v.X, (float)v.Y, (float)v.Z);

    private static Quaternion ToFloat(doubleQuat q) => new((float)q.X, (float)q.Y, (float)q.Z, (float)q.W);

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

        return _device.CreateDescriptorPool(
            new VkDescriptorPoolCreateInfo { MaxSets = Renderer.FramesInFlight, PoolSizeCount = 1, PoolSizes = &size },
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

    // Spheres draw a fullscreen triangle and markers a camera-facing quad: no culling. Both write gl_FragDepth.
    private VkPipeline CreatePipeline(VkRenderPass renderPass)
    {
        var vert = ShaderLoader.Load(_device, "planet.vert", ShaderKind.VertexShader);
        var frag = ShaderLoader.Load(_device, "planet.frag", ShaderKind.FragmentShader);

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

    /// <summary>Waits for the device, then destroys everything. Call before the Renderer is disposed.</summary>
    public void Dispose()
    {
        _device.WaitIdle();
        _device.DestroyPipeline(_pipeline, null);
        _device.DestroyPipelineLayout(_layout, null);
        _device.DestroyDescriptorPool(_pool, null);
        _device.DestroyDescriptorSetLayout(_setLayout, null);
        _uniforms.Dispose();
    }
}
