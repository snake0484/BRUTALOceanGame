using Brutal;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace BrutalGame.Engine.Rendering;

/// <summary>
/// Swapchain plus per-image views, UI-pass framebuffers and render-finished semaphores. The scene is drawn offscreen
/// (<see cref="SceneTarget"/>) and copied or blitted in; only the UI draws into these images.
/// Per-image semaphores avoid reusing a semaphore the presentation engine may still hold.
/// </summary>
public sealed unsafe class Swapchain : IDisposable
{
    public const VkFormat DepthFormat = VkFormat.D32SFloat;

    public VkSwapchainKHR Handle { get; private set; }
    public VkExtent2D Extent { get; private set; }
    public int2 RequestedSize { get; private set; }
    public int ImageCount => _images.Length;
    public int MinImageCount { get; private set; }
    public readonly VkSurfaceFormatKHR Format;
    public VkPresentModeKHR PresentMode;

    private readonly VulkanContext _context;
    private readonly VkRenderPass _renderPass;
    private VkImage[] _images = [];
    private VkImageView[] _views = [];
    private VkFramebuffer[] _framebuffers = [];
    private VkSemaphore[] _renderFinished = [];

    public Swapchain(VulkanContext context, VkSurfaceFormatKHR format, VkPresentModeKHR presentMode, VkRenderPass renderPass, int2 size)
    {
        _context = context;
        _renderPass = renderPass;
        Format = format;
        PresentMode = presentMode;
        Recreate(size);
    }

    public VkFramebuffer Framebuffer(int imageIndex) => _framebuffers[imageIndex];

    public VkImage Image(int imageIndex) => _images[imageIndex];

    public VkSemaphore RenderFinished(int imageIndex) => _renderFinished[imageIndex];

    public static VkSurfaceFormatKHR ChooseFormat(VulkanContext context)
    {
        var device = context.PhysicalDevice;
        Span<VkSurfaceFormatKHR> formats = stackalloc VkSurfaceFormatKHR[device.GetSurfaceFormatsKHR_Count(context.Surface)];
        device.GetSurfaceFormatsKHR_Write(context.Surface, formats);

        foreach (var f in formats)
            if (f.Format == VkFormat.B8G8R8A8UNorm && f.ColorSpace == VkColorSpaceKHR.SRGBNonlinearKHR)
                return f;

        return formats[0];
    }

    public static VkPresentModeKHR ChoosePresentMode(VulkanContext context, bool vsync)
    {
        if (vsync)
            return VkPresentModeKHR.FifoKHR;

        var device = context.PhysicalDevice;
        Span<VkPresentModeKHR> modes = stackalloc VkPresentModeKHR[device.GetSurfacePresentModesKHR_Count(context.Surface)];
        device.GetSurfacePresentModesKHR_Write(context.Surface, modes);

        var fallback = VkPresentModeKHR.FifoKHR;
        foreach (var mode in modes)
        {
            if (mode == VkPresentModeKHR.MailboxKHR)
                return mode;

            if (mode == VkPresentModeKHR.ImmediateKHR)
                fallback = mode;
        }

        return fallback;
    }

    /// <summary>
    /// Rebuilds for the given size. Returns false and keeps the old swapchain when the surface is zero-sized (minimised).
    /// </summary>
    public bool Recreate(int2 size)
    {
        var device = _context.Device;
        var caps = _context.PhysicalDevice.GetSurfaceCapabilitiesKHR(_context.Surface);

        var extent = caps.CurrentExtent.Width != -1
            ? caps.CurrentExtent
            : new VkExtent2D
            {
                Width = Math.Clamp(size.X, caps.MinImageExtent.Width, caps.MaxImageExtent.Width),
                Height = Math.Clamp(size.Y, caps.MinImageExtent.Height, caps.MaxImageExtent.Height),
            };

        if (extent.Width == 0 || extent.Height == 0)
            return false;

        Extent = extent;
        RequestedSize = size;

        MinImageCount = caps.MinImageCount + 1;
        if (caps.MaxImageCount > 0)
            MinImageCount = Math.Min(MinImageCount, caps.MaxImageCount);

        var old = Handle;
        Handle = device.CreateSwapchainKHR(
            new VkSwapchainCreateInfoKHR
            {
                Surface = _context.Surface,
                MinImageCount = MinImageCount,
                ImageFormat = Format.Format,
                ImageColorSpace = Format.ColorSpace,
                ImageExtent = Extent,
                ImageArrayLayers = 1,
                ImageUsage = VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferDstBit,
                ImageSharingMode = VkSharingMode.Exclusive,
                PreTransform = caps.CurrentTransform,
                CompositeAlpha = VkCompositeAlphaFlagsKHR.OpaqueBitKHR,
                PresentMode = PresentMode,
                Clipped = true,
                OldSwapchain = old,
            },
            null
        );

        DestroyImageResources();

        if (!old.IsNull())
            device.DestroySwapchainKHR(old, null);

        CreateImageResources();
        return true;
    }

    private void CreateImageResources()
    {
        var device = _context.Device;
        var count = device.GetSwapchainImagesKHR_Count(Handle);
        _images = new VkImage[count];
        _views = new VkImageView[count];
        _framebuffers = new VkFramebuffer[count];
        _renderFinished = new VkSemaphore[count];
        device.GetSwapchainImagesKHR_Write(Handle, _images);

        for (var i = 0; i < count; i++)
        {
            _views[i] = device.CreateImageView(
                new VkImageViewCreateInfo
                {
                    Image = _images[i],
                    ViewType = VkImageViewType._2D,
                    Format = Format.Format,
                    SubresourceRange = new VkImageSubresourceRange
                    {
                        AspectMask = VkImageAspectFlags.ColorBit,
                        LevelCount = 1,
                        LayerCount = 1,
                    },
                },
                null
            );

            var view = _views[i];
            _framebuffers[i] = device.CreateFramebuffer(
                new VkFramebufferCreateInfo
                {
                    RenderPass = _renderPass,
                    AttachmentCount = 1,
                    Attachments = &view,
                    Width = Extent.Width,
                    Height = Extent.Height,
                    Layers = 1,
                },
                null
            );

            _renderFinished[i] = device.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
        }
    }

    private void DestroyImageResources()
    {
        var device = _context.Device;

        for (var i = 0; i < _images.Length; i++)
        {
            device.DestroyFramebuffer(_framebuffers[i], null);
            device.DestroyImageView(_views[i], null);
            device.DestroySemaphore(_renderFinished[i], null);
        }
    }

    public void Dispose()
    {
        DestroyImageResources();
        _context.Device.DestroySwapchainKHR(Handle, null);
    }
}
