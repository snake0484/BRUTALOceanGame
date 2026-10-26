using System.Runtime.InteropServices;
using Brutal.GlfwApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.VulkanApi.Vma;

namespace BrutalGame.Engine.Rendering;

/// <summary>
/// Owns instance, surface, device, VMA allocator and the graphics/present queue.
/// </summary>
public sealed unsafe class VulkanContext : IDisposable
{
    private const VulkanHelpers.Api ApiVersion = VulkanHelpers.Api.VERSION_1_3;

    public readonly Instance Instance;
    public readonly VkSurfaceKHR Surface;
    public readonly PhysicalDevice PhysicalDevice;
    public readonly DeviceEx Device;
    public readonly Queue GraphicsQueue;
    public readonly VmaAllocator Allocator;
    public readonly bool ValidationEnabled;
    public readonly string DeviceName;
    private readonly VkDebugUtilsMessengerEXT _messenger;

    public VulkanContext(GlfwWindow window, bool enableValidation)
    {
        ValidationEnabled = enableValidation && VulkanHelpers.Validation.IsSupported;
        Instance = CreateInstance(ValidationEnabled);

        if (ValidationEnabled)
        {
            var callback = new PfnVkDebugUtilsMessengerCallbackEXT(&VulkanHelpers.DebugUtilsMessengerCallbacks.Default);
            _messenger = Instance.CreateDebugUtilsMessengerEXT(
                VulkanHelpers.BuildDefaultDebugMessengerCreateInfo(callback),
                null
            );
        }

        if (Glfw.CreateWindowSurface(Instance.VkHandle, window, out var surface) != 0)
            throw new InvalidOperationException("Failed to create window surface");

        Surface = new VkSurfaceKHR((nint)surface);

        PhysicalDevice = PickPhysicalDevice(out var queueFamily);
        DeviceName = PhysicalDevice.GetDeviceName();
        Device = CreateDevice(PhysicalDevice, queueFamily);
        GraphicsQueue = Device.GetQueue(queueFamily, 0);
        Allocator = Device.CreateVmaAllocator(new VmaAllocatorCreateInfo
        {
            PhysicalDevice = PhysicalDevice.Handle,
            Device = Device.Handle,
            VulkanApiVersion = (uint)ApiVersion,
        });

        Vulkan.Log.Info($"GPU: {DeviceName}, validation: {ValidationEnabled}");
    }

    private static Instance CreateInstance(bool validation)
    {
        var extensions = new HashSet<string>();
        var glfwExtensions = Glfw.GetRequiredInstanceExtensions(out var count);
        for (var i = 0; i < count; i++)
            extensions.Add(Marshal.PtrToStringUTF8((nint)glfwExtensions[i])!);

        VulkanHelpers.AddPlatformRequiredExtensions(extensions);

        var layers = new HashSet<string>();
        if (validation)
        {
            extensions.Add(VK.EXT_DEBUG_UTILS_EXTENSION_NAME);
            VulkanHelpers.AddValidationLayers(layers);
        }

        fixed (byte* name = "BRUTALGame"u8)
        {
            return Vulkan.CreateInstance(new Instance.CreateInfo
            {
                AppInfo = new VkApplicationInfo
                {
                    ApplicationName = name,
                    EngineName = name,
                    ApiVersion = (int)ApiVersion,
                },
                Flags = VulkanHelpers.GetPlatformRequiredInstanceCreateFlags(),
                EnabledExtensions = extensions,
                EnabledLayers = layers,
            });
        }
    }

    private PhysicalDevice PickPhysicalDevice(out int queueFamily)
    {
        PhysicalDevice? best = null;
        var bestScore = -1;
        queueFamily = -1;

        foreach (var device in Instance.PhysicalDevices)
        {
            if (!device.SupportsExtension("VK_KHR_swapchain"u8))
                continue;

            var properties = device.GetProperties();
            if (properties.ApiVersion < (int)ApiVersion)
                continue;

            var family = FindGraphicsPresentFamily(device);
            if (family < 0)
                continue;

            var score = properties.DeviceType switch
            {
                VkPhysicalDeviceType.DiscreteGpu => 2,
                VkPhysicalDeviceType.IntegratedGpu => 1,
                _ => 0,
            };

            if (score <= bestScore)
                continue;

            best = device;
            bestScore = score;
            queueFamily = family;
        }

        return best ?? throw new InvalidOperationException("No Vulkan 1.3 GPU with graphics + present support");
    }

    private int FindGraphicsPresentFamily(PhysicalDevice device)
    {
        Span<VkQueueFamilyProperties> families = stackalloc VkQueueFamilyProperties[device.GetQueueFamilyProperties_Count()];
        device.GetQueueFamilyProperties_Write(families);

        for (var i = 0; i < families.Length; i++)
        {
            if (!families[i].QueueFlags.HasFlag(VkQueueFlags.GraphicsBit))
                continue;

            if (device.GetSurfaceSupportKHR(i, Surface))
                return i;
        }

        return -1;
    }

    private static DeviceEx CreateDevice(PhysicalDevice physicalDevice, int queueFamily)
    {
        var priority = 1f;
        Span<VkDeviceQueueCreateInfo> queues =
        [
            new VkDeviceQueueCreateInfo
            {
                QueueFamilyIndex = queueFamily,
                QueueCount = 1,
                QueuePriorities = &priority,
            },
        ];

        var extensions = new HashSet<string>();
        VulkanHelpers.AddSwapchainExtensions(extensions);
        VulkanHelpers.AddMandatoryIfSupportedDeviceExtensions(extensions, physicalDevice);

        // Core 1.3 features our shaders rely on: shaderc emits OpDemoteToHelperInvocation for `discard`.
        var features13 = new VkPhysicalDeviceVulkan13Features { ShaderDemoteToHelperInvocation = true };

        return physicalDevice.CreateDevice(new DeviceEx.CreateInfo
        {
            Extensions = extensions,
            QueueCreateInfos = queues,
            Next = &features13,
        });
    }

    public void Dispose()
    {
        Device.WaitIdle();
        Device.DestroyVmaAllocator(Allocator);
        Device.Dispose();
        Instance.DestroySurfaceKHR(Surface, null);

        if (ValidationEnabled)
            Instance.DestroyDebugUtilsMessengerEXT(_messenger, null);

        Instance.Dispose();
    }
}
