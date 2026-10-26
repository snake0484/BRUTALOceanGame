using Brutal;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.ImGuiApi.Backends;
using Brutal.VulkanApi;

namespace BrutalGame.Engine.Rendering;

/// <summary>
/// Dear ImGui context with GLFW platform and Vulkan renderer backends, drawn in the swapchain pass.
/// </summary>
public sealed class ImGuiLayer : IDisposable
{
    private const int DescriptorPoolSize = 16;

    private readonly ImGuiContextPtr _context;
    private readonly GlfwBackend _platform;
    private readonly VulkanBackend _backend;

    public ImGuiLayer(GlfwWindow window, Renderer renderer)
    {
        _context = ImGui.CreateContext();
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard | ImGuiConfigFlags.DockingEnable;
        ImGui.StyleColorsDark();

        _platform = new GlfwBackend(window, GlfwBackend.ClientApi.Vulkan);
        _backend = new VulkanBackend(new VulkanBackend.CreateInfo
        {
            Device = renderer.Context.Device,
            GraphicsQueue = renderer.Context.GraphicsQueue,
            RenderPass = renderer.RenderPass,
            MinImageCount = Math.Max(2, renderer.Swapchain.MinImageCount),
            ImageCount = Math.Max(2, renderer.Swapchain.ImageCount),
            SampleCount = VkSampleCountFlags._1Bit,
            DescriptorPoolSize = DescriptorPoolSize,
            MinAllocationSize = ByteSize.FromMegabytes(1),
        });
    }

    public bool WantCaptureKeyboard => ImGui.GetIO().WantCaptureKeyboard;

    public bool WantCaptureMouse => ImGui.GetIO().WantCaptureMouse;

    public void NewFrame()
    {
        ImString.SharedStorage.Reset();
        _platform.NewFrame();
        _backend.NewFrame();
        ImGui.NewFrame();
    }

    /// <summary>
    /// Finalises UI on the CPU. Call every frame, even when nothing is presented.
    /// </summary>
    public void EndFrame() => ImGui.Render();

    public void Draw(CommandBuffer commands) => _backend.RenderDrawData(ImGui.GetDrawData(), commands);

    public void Dispose()
    {
        _backend.Dispose();
        _platform.Dispose();
        ImGui.DestroyContext(_context);
    }
}
