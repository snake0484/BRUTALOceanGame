using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;

namespace BrutalGame.Engine.Rendering;

/// <summary>
/// The offscreen colour + depth the opaque and water passes draw into, at the render scale's size. One framebuffer serves
/// both passes (they are compatible); the colour is copied or blitted to the swapchain image afterwards.
/// </summary>
public sealed unsafe class SceneTarget : IDisposable
{
    private readonly VulkanContext _context;
    private readonly VkFormat _colourFormat;
    private readonly VkRenderPass _renderPass;
    private VkImage _colour;
    private VmaAllocation _colourAllocation;
    private VkImageView _colourView;
    private VkImage _depth;
    private VmaAllocation _depthAllocation;
    private VkImageView _depthView;

    public SceneTarget(VulkanContext context, VkFormat colourFormat, VkRenderPass renderPass)
    {
        _context = context;
        _colourFormat = colourFormat;
        _renderPass = renderPass;
    }

    public VkExtent2D Extent { get; private set; }

    public VkImage Colour => _colour;

    public VkFramebuffer Framebuffer { get; private set; }

    /// <summary>New images at <paramref name="extent"/>. The caller has waited for the device.</summary>
    public void Recreate(VkExtent2D extent)
    {
        Destroy();
        Extent = extent;
        _colourView = CreateImage(_colourFormat, VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferSrcBit, VkImageAspectFlags.ColorBit, out _colour, out _colourAllocation);
        _depthView = CreateImage(Swapchain.DepthFormat, VkImageUsageFlags.DepthStencilAttachmentBit, VkImageAspectFlags.DepthBit, out _depth, out _depthAllocation);

        var attachments = stackalloc VkImageView[] { _colourView, _depthView };
        Framebuffer = _context.Device.CreateFramebuffer(
            new VkFramebufferCreateInfo
            {
                RenderPass = _renderPass,
                AttachmentCount = 2,
                Attachments = attachments,
                Width = extent.Width,
                Height = extent.Height,
                Layers = 1,
            },
            null
        );
    }

    private VkImageView CreateImage(VkFormat format, VkImageUsageFlags usage, VkImageAspectFlags aspect, out VkImage image, out VmaAllocation allocation)
    {
        var result = _context.Allocator.CreateImage(
            new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,
                Format = format,
                Extent = new VkExtent3D { Width = Extent.Width, Height = Extent.Height, Depth = 1 },
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = VkSampleCountFlags._1Bit,
                Tiling = VkImageTiling.Optimal,
                Usage = usage,
                SharingMode = VkSharingMode.Exclusive,
                InitialLayout = VkImageLayout.Undefined,
            },
            new VmaAllocationCreateInfo { Usage = VmaMemoryUsage.AutoPreferDevice },
            out image,
            out allocation,
            out _
        );

        if (result != VkResult.Success)
            throw new InvalidOperationException($"vmaCreateImage failed: {result}");

        return _context.Device.CreateImageView(
            new VkImageViewCreateInfo
            {
                Image = image,
                ViewType = VkImageViewType._2D,
                Format = format,
                SubresourceRange = new VkImageSubresourceRange { AspectMask = aspect, LevelCount = 1, LayerCount = 1 },
            },
            null
        );
    }

    private void Destroy()
    {
        if (_colour.IsNull())
            return;

        var device = _context.Device;
        device.DestroyFramebuffer(Framebuffer, null);
        device.DestroyImageView(_colourView, null);
        _context.Allocator.DestroyImage(_colour, _colourAllocation);
        device.DestroyImageView(_depthView, null);
        _context.Allocator.DestroyImage(_depth, _depthAllocation);
        _colour = default;
        _depth = default;
    }

    public void Dispose() => Destroy();
}
