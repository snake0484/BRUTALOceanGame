using System.Diagnostics;
using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using BrutalGame.Engine.Time;

namespace BrutalGame.Engine.Rendering;

/// <summary>
/// Frame loop: wait, acquire, record an opaque pass (cleared) and a water pass over it, submit, present. Between the two
/// the frame's colour and depth are left readable by transfer (ScreenReflections copies both for the water to sample).
/// The passes are compatible: pipelines are made against <see cref="RenderPass"/> and drawn in either.
/// </summary>
public sealed unsafe class Renderer : IDisposable
{
    public const int FramesInFlight = 2;
    private static readonly nint NoTimeout = nint.MaxValue;

    public readonly VulkanContext Context;
    public readonly Swapchain Swapchain;
    public readonly VkRenderPass RenderPass;
    private readonly VkRenderPass _opaquePass;
    public readonly GpuTimer GpuTimer;
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

    private struct FrameData
    {
        public VkCommandPool Pool;
        public CommandBuffer Commands;
        public VkFence InFlight;
        public VkSemaphore ImageAvailable;
    }

    public Renderer(GlfwWindow window, FrameStats stats, bool enableValidation, bool vsync)
    {
        _window = window;
        _stats = stats;
        _vsync = vsync;
        Context = new VulkanContext(window, enableValidation);

        var format = Swapchain.ChooseFormat(Context);
        RenderPass = CreateRenderPass(format.Format, opaque: false);
        _opaquePass = CreateRenderPass(format.Format, opaque: true);
        Swapchain = new Swapchain(Context, format, Swapchain.ChoosePresentMode(Context, vsync), RenderPass, window.FramebufferSize);

        for (var i = 0; i < FramesInFlight; i++)
            _frames[i] = CreateFrame();

        GpuTimer = new GpuTimer(Context.Device, Context.GraphicsQueue.Family, FramesInFlight);
        _frameScope = GpuTimer.Register("Frame");
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

    public VkExtent2D Extent => Swapchain.Extent;

    /// <summary>This frame's swapchain image: in TRANSFER_SRC between <see cref="EndOpaquePass"/> and <see cref="BeginWaterPass"/>.</summary>
    public VkImage ColourImage => Swapchain.Image(_imageIndex);

    /// <summary>The depth image: in TRANSFER_SRC between <see cref="EndOpaquePass"/> and <see cref="BeginWaterPass"/>.</summary>
    public VkImage DepthImage => Swapchain.DepthImage;

    /// <summary>
    /// The opaque pass clears both attachments and leaves both in TRANSFER_SRC; the
    /// water pass loads them from there and presents. Same formats and samples, so the two are compatible.
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
                InitialLayout = opaque ? VkImageLayout.Undefined : VkImageLayout.TransferSrcOptimal,
                FinalLayout = opaque ? VkImageLayout.TransferSrcOptimal : VkImageLayout.PresentSrcKHR,
            },
            new VkAttachmentDescription
            {
                Format = Swapchain.DepthFormat,
                Samples = VkSampleCountFlags._1Bit,
                LoadOp = load,
                StoreOp = opaque ? VkAttachmentStoreOp.Store : VkAttachmentStoreOp.DontCare,
                StencilLoadOp = VkAttachmentLoadOp.DontCare,
                StencilStoreOp = VkAttachmentStoreOp.DontCare,
                InitialLayout = opaque ? VkImageLayout.Undefined : VkImageLayout.TransferSrcOptimal,
                FinalLayout = opaque ? VkImageLayout.TransferSrcOptimal : VkImageLayout.DepthStencilAttachmentOptimal,
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
        var dependencies = stackalloc VkSubpassDependency[2];

        if (opaque)
        {
            // Shared depth image: previous frame's depth writes must finish before this frame clears it.
            dependencies[0] = new VkSubpassDependency
            {
                SrcSubpass = VK.SUBPASS_EXTERNAL,
                DstSubpass = 0,
                SrcStageMask = stages,
                DstStageMask = stages,
                SrcAccessMask = VkAccessFlags.DepthStencilAttachmentWriteBit,
                DstAccessMask = writes,
            };

            // The colour and depth are copied after the pass.
            dependencies[1] = new VkSubpassDependency
            {
                SrcSubpass = 0,
                DstSubpass = VK.SUBPASS_EXTERNAL,
                SrcStageMask = stages,
                DstStageMask = VkPipelineStageFlags.TransferBit,
                SrcAccessMask = writes,
                DstAccessMask = VkAccessFlags.TransferReadBit,
            };
        }
        else
        {
            // After the opaque pass and the copies out of it (skipped while reflections are off).
            dependencies[0] = new VkSubpassDependency
            {
                SrcSubpass = VK.SUBPASS_EXTERNAL,
                DstSubpass = 0,
                SrcStageMask = stages | VkPipelineStageFlags.TransferBit,
                DstStageMask = stages,
                SrcAccessMask = writes,
                DstAccessMask = reads | writes,
            };
        }

        return Context.Device.CreateRenderPass(
            new VkRenderPassCreateInfo
            {
                AttachmentCount = 2,
                Attachments = attachments,
                SubpassCount = 1,
                Subpasses = &subpass,
                DependencyCount = opaque ? 2 : 1,
                Dependencies = dependencies,
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
        GpuTimer.Begin(commands, _frameScope);
        return true;
    }

    /// <summary>Opens the opaque pass: the swapchain colour + depth, cleared.</summary>
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
                Framebuffer = Swapchain.Framebuffer(_imageIndex),
                RenderArea = new VkRect2D { Extent = Swapchain.Extent },
                ClearValueCount = 2,
                ClearValues = clear,
            },
            VkSubpassContents.Inline
        );
    }

    /// <summary>Closes the opaque pass: <see cref="ColourImage"/> is left in TRANSFER_SRC, the depth in SHADER_READ_ONLY.</summary>
    public void EndOpaquePass(CommandBuffer commands) => commands.EndRenderPass();

    /// <summary>Opens the water pass over the opaque one (water, rain, UI); <see cref="EndFrame"/> closes it.</summary>
    public void BeginWaterPass(CommandBuffer commands)
    {
        commands.BeginRenderPass(
            new VkRenderPassBeginInfo
            {
                RenderPass = RenderPass,
                Framebuffer = Swapchain.Framebuffer(_imageIndex),
                RenderArea = new VkRect2D { Extent = Swapchain.Extent },
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
        Swapchain.Dispose();
        device.DestroyRenderPass(RenderPass, null);
        device.DestroyRenderPass(_opaquePass, null);
        Context.Dispose();
    }
}
