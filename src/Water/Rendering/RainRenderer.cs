using System.Numerics;
using Brutal;
using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using BrutalGame.Engine.Rendering;

namespace BrutalGame.Water.Rendering;

/// <summary>
/// Rain (docs/rain.md): falling streaks in world-anchored boxes around the camera (rain.vert), and splash crowns where
/// drops land near it (rain_splash.comp). Both are stateless on the GPU but for the crown slots, rewritten every frame.
/// Drops, crowns and rings below the highest surface of their column (<see cref="RainCover"/>, written by the game each
/// frame) are hidden, so no rain falls under a roof or an overhang. The pipelines read the ocean's set 0 (frame, maps, cover)
/// beside their own set 1: rain is lit like the water, and crowns sit on the surface as drawn. The rings on the water
/// are the ocean's own (<see cref="OceanRenderer.Rain"/>).
/// </summary>
public sealed unsafe class RainRenderer : IDisposable
{
    public const int MaxDrops = 24576;
    public const float MinIntensity = 0.005f;

    private const int SplashSide = 64;
    private const int SplashSlots = SplashSide * SplashSide;

    /// <summary>Lanes per impact cell in rain_splash.comp, splitting its surface evaluation; 64 to a workgroup.</summary>
    private const int SplashLanes = 8;
    private const int SplashBytes = SplashSlots * 2 * 16;
    private const int HeaderFloats = 12;
    private const int FrameBytes = HeaderFloats * sizeof(float);

    /// <summary>m/s: mean terminal speed of 2–3 mm drops; each falls at 0.75..1.25 of it (rain.vert).</summary>
    private const float FallSpeed = 7.5f;

    /// <summary>s of motion blur per streak.</summary>
    private const float Exposure = 0.03f;

    /// <summary>m: the air's drift wraps at the far box's width (rain.vert), a multiple of the near box's.</summary>
    private const double AirWrap = 48.0;

    /// <summary>s: the drops' fall and the impacts restart, unseen among so many, every 17 minutes, keeping float precision.</summary>
    private const double TimeLoop = 1024.0;

    private readonly Device _device;
    private readonly OceanRenderer _ocean;
    private readonly VkDescriptorSetLayout _setLayout;
    private readonly VkDescriptorPool _pool;
    private readonly VkDescriptorSet[] _sets = new VkDescriptorSet[Renderer.FramesInFlight];
    private readonly GpuBuffer[] _frames = new GpuBuffer[Renderer.FramesInFlight];
    private readonly GpuBuffer _splashes;
    private readonly VkPipelineLayout _layout;
    private readonly VkPipeline _dropPipeline;
    private readonly VkPipeline _splashPipeline;
    private readonly VkPipeline _splashUpdate;
    private readonly GpuTimer _timer;
    private readonly int _dropScope;
    private readonly int _splashScope;
    private readonly int _updateScope;
    private double _airX;
    private double _airZ;
    private int _drops;
    private bool _active;

    /// <summary>0..1, from the weather at the camera.</summary>
    public float Intensity;

    /// <summary>Wind (m/s along x, z) that carries the drops.</summary>
    public Vector2 Wind;

    public RainRenderer(Renderer renderer, OceanRenderer ocean)
    {
        _device = renderer.Context.Device;
        _ocean = ocean;

        for (var i = 0; i < Renderer.FramesInFlight; i++)
            _frames[i] = new GpuBuffer(renderer.Context, FrameBytes, VkBufferUsageFlags.StorageBufferBit, true);

        _splashes = new GpuBuffer(renderer.Context, SplashBytes, VkBufferUsageFlags.StorageBufferBit, false);
        _setLayout = CreateSetLayout();
        _pool = CreatePool();
        CreateSets();
        _layout = _device.CreatePipelineLayout([ocean.SetLayout, _setLayout], [], null);
        _dropPipeline = CreatePipeline(renderer.RenderPass, "rain.vert", "rain.frag");
        _splashPipeline = CreatePipeline(renderer.RenderPass, "rain_splash.vert", "rain_splash.frag");
        _splashUpdate = CreateComputePipeline("rain_splash.comp");

        _timer = renderer.GpuTimer;
        _updateScope = _timer.Register("Rain splash sim");
        _splashScope = _timer.Register("Rain splashes");
        _dropScope = _timer.Register("Rain drops");
    }

    /// <summary>The ocean's rain (intensity, looped time) as of the last <see cref="Prepare"/>, for the voxel water's rings.</summary>
    public Vector2 Surface => new(_ocean.Rain, _ocean.RainTime);

    /// <summary>Drops drawn this frame.</summary>
    public int Drops => _active ? _drops : 0;

    /// <summary>
    /// Writes this frame's rain block and the ocean's rain. Once per frame, before the ocean's Prepare, after the
    /// <see cref="RainCover"/> is written.
    /// </summary>
    /// <param name="pixelAngle">Radians a pixel spans: streaks never get thinner than about a pixel.</param>
    public void Prepare(int frameIndex, double renderTime, float frameSeconds, float pixelAngle)
    {
        _active = Intensity > MinIntensity;
        _ocean.Rain = _active ? Intensity : 0f;
        _ocean.RainTime = (float)(renderTime % TimeLoop);

        if (!_active)
            return;

        _airX = (_airX + Wind.X * frameSeconds) % AirWrap;
        _airZ = (_airZ + Wind.Y * frameSeconds) % AirWrap;
        _drops = (int)(MaxDrops * MathF.Pow(Intensity, 0.8f));

        ReadOnlySpan<float> header =
        [
            Intensity, _ocean.RainTime, Exposure, pixelAngle,
            (float)_airX, FallSpeed, (float)_airZ, 0f,
            Wind.X, Wind.Y, 0f, 0f,
        ];
        header.CopyTo(new Span<float>(_frames[frameIndex].Mapped, HeaderFloats));
    }

    /// <summary>Places this frame's splash crowns. Outside a render pass, after the ocean's Prepare (it reads the surface).</summary>
    public void RecordSplashes(CommandBuffer commands, int frameIndex)
    {
        if (!_active)
            return;

        // The previous frame's draw read the slots; this frame's compute rewrites them.
        Barrier(commands, VkPipelineStageFlags.VertexShaderBit, VkAccessFlags.ShaderReadBit, VkPipelineStageFlags.ComputeShaderBit, VkAccessFlags.ShaderWriteBit);
        commands.BindPipeline(VkPipelineBindPoint.Compute, _splashUpdate);
        commands.BindDescriptorSets(VkPipelineBindPoint.Compute, _layout, 0, [_ocean.Set(frameIndex), _sets[frameIndex]], []);
        _timer.Begin(commands, _updateScope);
        commands.Dispatch(SplashSlots * SplashLanes / 64, 1, 1);
        _timer.End(commands, _updateScope);
        Barrier(commands, VkPipelineStageFlags.ComputeShaderBit, VkAccessFlags.ShaderWriteBit, VkPipelineStageFlags.VertexShaderBit, VkAccessFlags.ShaderReadBit);
    }

    /// <summary>Crowns, then the falling drops over them. Inside the main pass, after the water.</summary>
    public void Record(CommandBuffer commands, int frameIndex, VkExtent2D extent)
    {
        if (!_active)
            return;

        commands.BindDescriptorSets(VkPipelineBindPoint.Graphics, _layout, 0, [_ocean.Set(frameIndex), _sets[frameIndex]], []);
        commands.SetViewport(0, [new VkViewport { Width = extent.Width, Height = extent.Height, MaxDepth = 1f }]);
        commands.SetScissor(0, [new VkRect2D { Extent = extent }]);

        _timer.Begin(commands, _splashScope);
        commands.BindPipeline(VkPipelineBindPoint.Graphics, _splashPipeline);
        commands.Draw(6, SplashSlots, 0, 0);
        _timer.End(commands, _splashScope);

        if (_drops == 0)
            return;

        _timer.Begin(commands, _dropScope);
        commands.BindPipeline(VkPipelineBindPoint.Graphics, _dropPipeline);
        commands.Draw(6, _drops, 0, 0);
        _timer.End(commands, _dropScope);
    }

    private static void Barrier(CommandBuffer commands, VkPipelineStageFlags srcStage, VkAccessFlags srcAccess, VkPipelineStageFlags dstStage, VkAccessFlags dstAccess) =>
        commands.PipelineBarrier(srcStage, dstStage, VkDependencyFlags.None, [new VkMemoryBarrier { SrcAccessMask = srcAccess, DstAccessMask = dstAccess }], [], []);

    private VkDescriptorSetLayout CreateSetLayout()
    {
        const VkShaderStageFlags stages = VkShaderStageFlags.VertexBit | VkShaderStageFlags.ComputeBit;
        var bindings = stackalloc VkDescriptorSetLayoutBinding[]
        {
            new() { Binding = 0, DescriptorType = VkDescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = stages },
            new() { Binding = 1, DescriptorType = VkDescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = stages },
        };

        return _device.CreateDescriptorSetLayout(new VkDescriptorSetLayoutCreateInfo { BindingCount = 2, Bindings = bindings }, null);
    }

    private VkDescriptorPool CreatePool()
    {
        var size = new VkDescriptorPoolSize { Type = VkDescriptorType.StorageBuffer, DescriptorCount = Renderer.FramesInFlight * 2 };
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
            new VkDescriptorSetAllocateInfo { DescriptorPool = _pool, DescriptorSetCount = Renderer.FramesInFlight, SetLayouts = layouts },
            _sets
        );

        var splashes = new VkDescriptorBufferInfo { Buffer = _splashes.Handle, Range = (ByteSize64)SplashBytes };
        Span<VkWriteDescriptorSet> writes = stackalloc VkWriteDescriptorSet[2];

        for (var i = 0; i < Renderer.FramesInFlight; i++)
        {
            var frame = new VkDescriptorBufferInfo { Buffer = _frames[i].Handle, Range = (ByteSize64)FrameBytes };
            writes[0] = new VkWriteDescriptorSet { DstSet = _sets[i], DstBinding = 0, DescriptorCount = 1, DescriptorType = VkDescriptorType.StorageBuffer, BufferInfo = &frame };
            writes[1] = new VkWriteDescriptorSet { DstSet = _sets[i], DstBinding = 1, DescriptorCount = 1, DescriptorType = VkDescriptorType.StorageBuffer, BufferInfo = &splashes };
            _device.UpdateDescriptorSets(writes, []);
        }
    }

    /// <summary>Alpha-blended, depth-tested but not written, no culling: thin quads turned to the camera.</summary>
    private VkPipeline CreatePipeline(VkRenderPass renderPass, string vertex, string fragment)
    {
        var vert = ShaderLoader.Load(_device, vertex, ShaderKind.VertexShader);
        var frag = ShaderLoader.Load(_device, fragment, ShaderKind.FragmentShader);

        try
        {
            fixed (byte* name = "main"u8)
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

    private VkPipeline CreateComputePipeline(string file)
    {
        var module = ShaderLoader.Load(_device, file, ShaderKind.ComputeShader);

        try
        {
            fixed (byte* name = "main"u8)
            {
                var pipeline = new VkPipeline();
                _device.CreateComputePipelines(
                    default,
                    [
                        new VkComputePipelineCreateInfo
                        {
                            Stage = new VkPipelineShaderStageCreateInfo { Stage = VkShaderStageFlags.ComputeBit, Module = module, Name = name },
                            Layout = _layout,
                        },
                    ],
                    null,
                    new ReadOnlySpan<VkPipeline>(&pipeline, 1)
                );
                return pipeline;
            }
        }
        finally
        {
            _device.DestroyShaderModule(module, null);
        }
    }

    public void Dispose()
    {
        _device.DestroyPipeline(_dropPipeline, null);
        _device.DestroyPipeline(_splashPipeline, null);
        _device.DestroyPipeline(_splashUpdate, null);
        _device.DestroyPipelineLayout(_layout, null);
        _device.DestroyDescriptorPool(_pool, null);
        _device.DestroyDescriptorSetLayout(_setLayout, null);
        _splashes.Dispose();

        foreach (var frame in _frames)
            frame.Dispose();
    }
}
