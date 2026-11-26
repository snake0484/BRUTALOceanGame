using System.Diagnostics;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using BrutalGame.Engine.Time;

namespace BrutalGame.Engine.Rendering;

/// <summary>
/// Frame loop: wait, acquire, record an opaque pass (cleared) and a water pass over it into the offscreen
/// <see cref="SceneTarget"/> at the render scale, bring that to the swapchain image (copied at scale 1, else a bilinear
/// blit), draw the UI over it at native resolution, submit, present. The scene passes are compatible: pipelines are made
/// against <see cref="RenderPass"/> and drawn in either; the UI's against <see cref="UiPass"/>.
/// </summary>
public sealed unsafe class Renderer : IDisposable
{
    public const int FramesInFlight = 2;
    public const float MinRenderScale = 0.5f;
    public const float MaxRenderScale = 1f;
    private static readonly nint NoTimeout = nint.MaxValue;

    public readonly VulkanContext Context;
    public readonly Swapchain Swapchain;
    public readonly VkRenderPass RenderPass;
    public readonly VkRenderPass UiPass;
    private readonly VkRenderPass _opaquePass;
    private readonly SceneTarget _scene;
    public readonly GpuTimer GpuTimer;
    public readonly PipelineStats PipelineStats;
    public float4 ClearColor = new(0.02f, 0.05f, 0.09f, 1f);

    private readonly GlfwWindow _window;
    private readonly FrameStats _stats;
    private readonly FrameData[] _frames = new FrameData[FramesInFlight];
    private int _frameIndex;
    private int _imageIndex;
    private bool _needsRecreate;
    private bool _vsync;
    private long _recordStart;
    private readonly int _frameScope;
    private readonly int _upscaleScope;
    private float _renderScale;
    private GpuBuffer? _capture;
    private VkExtent2D _captureExtent;
    private bool _captureRequested;

    private struct FrameData
    {
        public VkCommandPool Pool;
        public CommandBuffer Commands;
        public VkFence InFlight;
        public VkSemaphore ImageAvailable;
    }

    /// <param name="renderScale">Scene resolution per axis relative to the window, <see cref="MinRenderScale"/> to 1.</param>
    public Renderer(GlfwWindow window, FrameStats stats, bool enableValidation, bool vsync, float renderScale = 1f)
    {
        _window = window;
        _stats = stats;
        _vsync = vsync;
        _renderScale = Math.Clamp(renderScale, MinRenderScale, MaxRenderScale);
        Context = new VulkanContext(window, enableValidation);

        // The scene keeps the swapchain's format (UNORM, written as is, no tonemap), so a copy at scale 1 is exact.
        var format = Swapchain.ChooseFormat(Context);
        RenderPass = CreateRenderPass(format.Format, opaque: false);
        _opaquePass = CreateRenderPass(format.Format, opaque: true);
        UiPass = CreateUiPass(format.Format);
        Swapchain = new Swapchain(Context, format, Swapchain.ChoosePresentMode(Context, vsync), UiPass, window.FramebufferSize);
        _scene = new SceneTarget(Context, format.Format, RenderPass);
        _scene.Recreate(ScaledExtent(Swapchain.Extent, _renderScale));

        for (var i = 0; i < FramesInFlight; i++)
            _frames[i] = CreateFrame();

        GpuTimer = new GpuTimer(Context.Device, Context.GraphicsQueue.Family, FramesInFlight);
        _frameScope = GpuTimer.Register("Frame");
        _upscaleScope = GpuTimer.Register("Upscale");
        PipelineStats = new PipelineStats(Context, FramesInFlight);
    }

    public bool VSync
    {
        get => _vsync;
        set
        {
            if (value == _vsync)
                return;

            _vsync = value;
            Swapchain.PresentMode = Swapchain.ChoosePresentMode(Context, value);
            _needsRecreate = true;
        }
    }

    public VkPresentModeKHR PresentMode => Swapchain.PresentMode;

    public int FrameIndex => _frameIndex;

    /// <summary>
    /// Scene resolution per axis relative to the window (<see cref="MinRenderScale"/> to 1). A change rebuilds the scene
    /// target at the next <see cref="BeginFrame"/>.
    /// </summary>
    public float RenderScale
    {
        get => _renderScale;
        set => _renderScale = Math.Clamp(value, MinRenderScale, MaxRenderScale);
    }

    /// <summary>The scene's size (the render scale's share of the window): every scene pass draws at this.</summary>
    public VkExtent2D Extent => _scene.Extent;

    /// <summary>The swapchain's size: the UI draws at this.</summary>
    public VkExtent2D OutputExtent => Swapchain.Extent;

    private static VkExtent2D ScaledExtent(VkExtent2D extent, float scale) => new()
    {
        Width = Math.Max(1, (int)MathF.Round(extent.Width * scale)),
        Height = Math.Max(1, (int)MathF.Round(extent.Height * scale)),
    };

    /// <summary>
    /// The opaque pass clears both attachments and leaves them as attachments; the water pass loads them and leaves the
    /// colour in TRANSFER_SRC for the swapchain. Same formats and samples, so the two are compatible.
    /// </summary>
    private VkRenderPass CreateRenderPass(VkFormat format, bool opaque)
    {
        var load = opaque ? VkAttachmentLoadOp.Clear : VkAttachmentLoadOp.Load;
        var attachments = stackalloc VkAttachmentDescription[]
        {
            new VkAttachmentDescription
            {
                Format = format,
                Samples = VkSampleCountFlags._1Bit,
                LoadOp = load,
                StoreOp = VkAttachmentStoreOp.Store,
                StencilLoadOp = VkAttachmentLoadOp.DontCare,
                StencilStoreOp = VkAttachmentStoreOp.DontCare,
                InitialLayout = opaque ? VkImageLayout.Undefined : VkImageLayout.ColorAttachmentOptimal,
                FinalLayout = opaque ? VkImageLayout.ColorAttachmentOptimal : VkImageLayout.TransferSrcOptimal,
            },
            new VkAttachmentDescription
            {
                Format = Swapchain.DepthFormat,
                Samples = VkSampleCountFlags._1Bit,
                LoadOp = load,
                StoreOp = opaque ? VkAttachmentStoreOp.Store : VkAttachmentStoreOp.DontCare,
                StencilLoadOp = VkAttachmentLoadOp.DontCare,
                StencilStoreOp = VkAttachmentStoreOp.DontCare,
                InitialLayout = opaque ? VkImageLayout.Undefined : VkImageLayout.DepthStencilAttachmentOptimal,
                FinalLayout = VkImageLayout.DepthStencilAttachmentOptimal,
            },
        };

        var colorRef = new VkAttachmentReference { Attachment = 0, Layout = VkImageLayout.ColorAttachmentOptimal };
        var depthRef = new VkAttachmentReference { Attachment = 1, Layout = VkImageLayout.DepthStencilAttachmentOptimal };

        var subpass = new VkSubpassDescription
        {
            PipelineBindPoint = VkPipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            ColorAttachments = &colorRef,
            DepthStencilAttachment = &depthRef,
        };

        const VkPipelineStageFlags stages =
            VkPipelineStageFlags.ColorAttachmentOutputBit
            | VkPipelineStageFlags.EarlyFragmentTestsBit
            | VkPipelineStageFlags.LateFragmentTestsBit;

        const VkAccessFlags writes = VkAccessFlags.ColorAttachmentWriteBit | VkAccessFlags.DepthStencilAttachmentWriteBit;
        const VkAccessFlags reads = VkAccessFlags.ColorAttachmentReadBit | VkAccessFlags.DepthStencilAttachmentReadBit;

        // Identical in both passes, as render pass compatibility requires. Into the pass: the previous frame's writes and
        // its copy to the swapchain (the shared images are cleared), or the opaque pass. Out of the pass: the colour copied
        // to the swapchain.
        var dependencies = stackalloc VkSubpassDependency[]
        {
            new VkSubpassDependency
            {
                SrcSubpass = VK.SUBPASS_EXTERNAL,
                DstSubpass = 0,
                SrcStageMask = stages | VkPipelineStageFlags.TransferBit,
                DstStageMask = stages,
                SrcAccessMask = writes,
                DstAccessMask = reads | writes,
            },
            new VkSubpassDependency
            {
                SrcSubpass = 0,
                DstSubpass = VK.SUBPASS_EXTERNAL,
                SrcStageMask = stages,
                DstStageMask = VkPipelineStageFlags.TransferBit,
                SrcAccessMask = writes,
                DstAccessMask = VkAccessFlags.TransferReadBit,
            },
        };

        return Context.Device.CreateRenderPass(
            new VkRenderPassCreateInfo
            {
                AttachmentCount = 2,
                Attachments = attachments,
                SubpassCount = 1,
                Subpasses = &subpass,
                DependencyCount = 2,
                Dependencies = dependencies,
            },
            null
        );
    }

    /// <summary>The swapchain image after the scene's copy: loaded from TRANSFER_DST, the UI drawn over it, presented.</summary>
    private VkRenderPass CreateUiPass(VkFormat format)
    {
        var attachment = new VkAttachmentDescription
        {
            Format = format,
            Samples = VkSampleCountFlags._1Bit,
            LoadOp = VkAttachmentLoadOp.Load,
            StoreOp = VkAttachmentStoreOp.Store,
            StencilLoadOp = VkAttachmentLoadOp.DontCare,
            StencilStoreOp = VkAttachmentStoreOp.DontCare,
            InitialLayout = VkImageLayout.TransferDstOptimal,
            FinalLayout = VkImageLayout.PresentSrcKHR,
        };

        var colorRef = new VkAttachmentReference { Attachment = 0, Layout = VkImageLayout.ColorAttachmentOptimal };
        var subpass = new VkSubpassDescription
        {
            PipelineBindPoint = VkPipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            ColorAttachments = &colorRef,
        };

        var dependency = new VkSubpassDependency
        {
            SrcSubpass = VK.SUBPASS_EXTERNAL,
            DstSubpass = 0,
            SrcStageMask = VkPipelineStageFlags.TransferBit,
            DstStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = VkAccessFlags.TransferWriteBit,
            DstAccessMask = VkAccessFlags.ColorAttachmentReadBit | VkAccessFlags.ColorAttachmentWriteBit,
        };

        return Context.Device.CreateRenderPass(
            new VkRenderPassCreateInfo
            {
                AttachmentCount = 1,
                Attachments = &attachment,
                SubpassCount = 1,
                Subpasses = &subpass,
                DependencyCount = 1,
                Dependencies = &dependency,
            },
            null
        );
    }

    private FrameData CreateFrame()
    {
        var device = Context.Device;
        var pool = device.CreateCommandPool(
            new VkCommandPoolCreateInfo
            {
                Flags = VkCommandPoolCreateFlags.TransientBit,
                QueueFamilyIndex = Context.GraphicsQueue.Family,
            },
            null
        );

        return new FrameData
        {
            Pool = pool,
            Commands = device.AllocateCommandBuffer(
                new VkCommandBufferAllocateInfo { CommandPool = pool, Level = VkCommandBufferLevel.Primary }
            ),
            InFlight = device.CreateFence(VkFenceCreateFlags.SignaledBit, null),
            ImageAvailable = device.CreateSemaphore(new VkSemaphoreCreateInfo(), null),
        };
    }

    /// <summary>
    /// Starts a frame and begins its command buffer. Record compute work, then call <see cref="BeginOpaquePass"/>.
    /// Returns false when nothing should be drawn.
    /// </summary>
    public bool BeginFrame(out CommandBuffer commands)
    {
        commands = default;
        var size = _window.FramebufferSize;

        if (size.X == 0 || size.Y == 0)
            return false;

        if (_needsRecreate || size.X != Swapchain.RequestedSize.X || size.Y != Swapchain.RequestedSize.Y)
        {
            if (!RecreateSwapchain(size))
                return false;
        }

        var device = Context.Device;
        var sceneExtent = ScaledExtent(Swapchain.Extent, _renderScale);

        // Rare (a resize or a new render scale), so it waits for the device rather than tracking frames.
        if (sceneExtent.Width != _scene.Extent.Width || sceneExtent.Height != _scene.Extent.Height)
        {
            device.WaitIdle();
            _scene.Recreate(sceneExtent);
        }

        ref var frame = ref _frames[_frameIndex];

        var waitStart = Stopwatch.GetTimestamp();
        device.WaitForFences([frame.InFlight], true, NoTimeout);
        _stats.GpuWait.Record(ElapsedMs(waitStart));

        int imageIndex;
        var result = device.PInvoke.vkAcquireNextImageKHR.Invoke(
            device.Handle,
            Swapchain.Handle,
            NoTimeout,
            frame.ImageAvailable,
            default,
            &imageIndex
        );

        if (result == VkResult.ErrorOutOfDateKHR)
        {
            _needsRecreate = true;
            return false;
        }

        if (result != VkResult.Success && result != VkResult.SuboptimalKHR)
            throw new VkResultError(result, $"vkAcquireNextImageKHR failed: {result}");

        _recordStart = Stopwatch.GetTimestamp();
        _imageIndex = imageIndex;
        device.ResetFences([frame.InFlight]);
        device.ResetCommandPool(frame.Pool, VkCommandPoolResetFlags.None);

        commands = frame.Commands;
        commands.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
        GpuTimer.BeginFrame(commands, _frameIndex);
        PipelineStats.BeginFrame(commands, _frameIndex);
        GpuTimer.Begin(commands, _frameScope);
        return true;
    }

    /// <summary>Opens the opaque pass: the scene colour + depth, cleared.</summary>
    public void BeginOpaquePass(CommandBuffer commands)
    {
        var clear = stackalloc VkClearValue[]
        {
            new VkClearValue { Color = new VkClearColorValue { Float32 = ClearColor } },
            new VkClearDepthStencilValue { Depth = 0f },
        };
        commands.BeginRenderPass(
            new VkRenderPassBeginInfo
            {
                RenderPass = _opaquePass,
                Framebuffer = _scene.Framebuffer,
                RenderArea = new VkRect2D { Extent = _scene.Extent },
                ClearValueCount = 2,
                ClearValues = clear,
            },
            VkSubpassContents.Inline
        );
    }

    /// <summary>Closes the opaque pass; <see cref="BeginWaterPass"/> draws over it.</summary>
    public void EndOpaquePass(CommandBuffer commands) => commands.EndRenderPass();

    /// <summary>Opens the water pass over the opaque one; <see cref="BeginUiPass"/> closes it.</summary>
    public void BeginWaterPass(CommandBuffer commands)
    {
        commands.BeginRenderPass(
            new VkRenderPassBeginInfo
            {
                RenderPass = RenderPass,
                Framebuffer = _scene.Framebuffer,
                RenderArea = new VkRect2D { Extent = _scene.Extent },
            },
            VkSubpassContents.Inline
        );
    }

    /// <summary>
    /// Test probes only (--seam-test): the next frame's scene colour is also copied to a host buffer for
    /// <see cref="ReadCapture"/>. The game's frame loop never asks for one: no readback there.
    /// </summary>
    public void RequestCapture() => _captureRequested = true;

    /// <summary>
    /// Waits for the device, then the captured scene: 4 bytes per pixel in the swapchain's channel order, rows top down.
    /// Empty if no frame has been captured.
    /// </summary>
    public ReadOnlySpan<byte> ReadCapture(out VkExtent2D extent)
    {
        extent = _captureExtent;

        if (_capture is null || _captureRequested)
            return default;

        Context.Device.WaitIdle();
        return new ReadOnlySpan<byte>(_capture.Mapped, extent.Width * extent.Height * 4);
    }

    /// <summary>After the water pass, with the scene colour in TRANSFER_SRC.</summary>
    private void RecordCapture(CommandBuffer commands)
    {
        var extent = _scene.Extent;
        var size = extent.Width * extent.Height * 4;

        if (_capture is null || _capture.Size < size)
        {
            Context.Device.WaitIdle();
            _capture?.Dispose();
            _capture = new GpuBuffer(Context, size, VkBufferUsageFlags.TransferDstBit, hostVisible: true, hostRead: true);
        }

        commands.CopyImageToBuffer(_scene.Colour, VkImageLayout.TransferSrcOptimal, _capture.Handle,
        [
            new VkBufferImageCopy
            {
                ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
                ImageExtent = new VkExtent3D { Width = extent.Width, Height = extent.Height, Depth = 1 },
            },
        ]);
        _captureExtent = extent;
        _captureRequested = false;
    }

    /// <summary>
    /// Closes the water pass, brings the scene to this frame's swapchain image (a copy at the same size, else a bilinear
    /// blit) and opens the UI pass over it at native resolution; <see cref="EndFrame"/> closes it.
    /// </summary>
    public void BeginUiPass(CommandBuffer commands)
    {
        commands.EndRenderPass();

        if (_captureRequested)
            RecordCapture(commands);

        GpuTimer.Begin(commands, _upscaleScope);

        // After the acquire semaphore's wait (colour attachment output); the image's old contents are discarded.
        var image = Swapchain.Image(_imageIndex);
        commands.PipelineBarrier(
            VkPipelineStageFlags.ColorAttachmentOutputBit,
            VkPipelineStageFlags.TransferBit,
            VkDependencyFlags.None,
            [],
            [],
            [
                new VkImageMemoryBarrier
                {
                    DstAccessMask = VkAccessFlags.TransferWriteBit,
                    OldLayout = VkImageLayout.Undefined,
                    NewLayout = VkImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
                    Image = image,
                    SubresourceRange = new VkImageSubresourceRange { AspectMask = VkImageAspectFlags.ColorBit, LevelCount = 1, LayerCount = 1 },
                },
            ]
        );

        var src = _scene.Extent;
        var dst = Swapchain.Extent;
        var layers = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 };

        if (src.Width == dst.Width && src.Height == dst.Height)
        {
            var size = new VkExtent3D { Width = src.Width, Height = src.Height, Depth = 1 };
            commands.CopyImage(_scene.Colour, VkImageLayout.TransferSrcOptimal, image, VkImageLayout.TransferDstOptimal,
                [new VkImageCopy { SrcSubresource = layers, DstSubresource = layers, Extent = size }]);
        }
        else
        {
            var blit = new VkImageBlit { SrcSubresource = layers, DstSubresource = layers };
            blit.SrcOffsets[1] = new VkOffset3D { X = src.Width, Y = src.Height, Z = 1 };
            blit.DstOffsets[1] = new VkOffset3D { X = dst.Width, Y = dst.Height, Z = 1 };
            commands.BlitImage(_scene.Colour, VkImageLayout.TransferSrcOptimal, image, VkImageLayout.TransferDstOptimal, [blit], VkFilter.Linear);
        }

        GpuTimer.End(commands, _upscaleScope);
        commands.BeginRenderPass(
            new VkRenderPassBeginInfo
            {
                RenderPass = UiPass,
                Framebuffer = Swapchain.Framebuffer(_imageIndex),
                RenderArea = new VkRect2D { Extent = dst },
            },
            VkSubpassContents.Inline
        );
    }

    public void EndFrame()
    {
        ref var frame = ref _frames[_frameIndex];
        var commands = frame.Commands;
        commands.EndRenderPass();
        GpuTimer.End(commands, _frameScope);
        commands.End();
        _stats.Record.Record(ElapsedMs(_recordStart));

        var presentStart = Stopwatch.GetTimestamp();
        var renderFinished = Swapchain.RenderFinished(_imageIndex);

        Context.GraphicsQueue.Submit(
            [frame.ImageAvailable],
            [VkPipelineStageFlags.ColorAttachmentOutputBit],
            [commands],
            [renderFinished],
            frame.InFlight
        );

        var swapchain = Swapchain.Handle;
        var imageIndex = _imageIndex;
        var presentInfo = new VkPresentInfoKHR
        {
            WaitSemaphoreCount = 1,
            WaitSemaphores = &renderFinished,
            SwapchainCount = 1,
            Swapchains = &swapchain,
            ImageIndices = &imageIndex,
        };

        // Raw call so out-of-date is a result, not an exception. Single submitting thread for now.
        var result = Context.Device.PInvoke.vkQueuePresentKHR.Invoke(Context.GraphicsQueue.Handle, &presentInfo);

        if (result == VkResult.ErrorOutOfDateKHR || result == VkResult.SuboptimalKHR)
            _needsRecreate = true;
        else if (result != VkResult.Success)
            throw new VkResultError(result, $"vkQueuePresentKHR failed: {result}");

        _stats.Present.Record(ElapsedMs(presentStart));
        _frameIndex = (_frameIndex + 1) % FramesInFlight;
    }

    private bool RecreateSwapchain(int2 size)
    {
        Context.Device.WaitIdle();
        _needsRecreate = !Swapchain.Recreate(size);
        return !_needsRecreate;
    }

    public void WaitIdle() => Context.Device.WaitIdle();

    private static float ElapsedMs(long start) => (float)Stopwatch.GetElapsedTime(start).TotalMilliseconds;

    public void Dispose()
    {
        var device = Context.Device;
        device.WaitIdle();

        foreach (var frame in _frames)
        {
            device.DestroyFence(frame.InFlight, null);
            device.DestroySemaphore(frame.ImageAvailable, null);
            device.DestroyCommandPool(frame.Pool, null);
        }

        GpuTimer.Dispose();
        PipelineStats.Dispose();
        _capture?.Dispose();
        _scene.Dispose();
        Swapchain.Dispose();
        device.DestroyRenderPass(UiPass, null);
        device.DestroyRenderPass(RenderPass, null);
        device.DestroyRenderPass(_opaquePass, null);
        Context.Dispose();
    }
}
