using Brutal.ShaderCApi;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace BrutalGame.Engine.Rendering;

/// <summary>
/// Compiles GLSL from the output shaders folder to SPIR-V at load time.
/// </summary>
public static unsafe class ShaderLoader
{
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "shaders");

    public static VkShaderModule Load(Device device, string fileName, ShaderKind kind)
    {
        var path = Path.Combine(Directory, fileName);
        using var compiler = ShaderC.CreateCompiler();
        using var options = ShaderC.CreateCompilerOptions();
        options.SetTargetEnv(TargetEnv.Vulkan, (uint)EnvVersion.Vulkan_1_3);
        options.SetOptimizationLevel(OptimizationLevel.Performance);

        // #include "x.glsl" resolves next to the including file (shared ocean surface code).
        options.SetDefaultIncludeCallbacks();

        using var result = compiler.CompileFile(path, kind, "main", options);
        var spirv = new ReadOnlySpan<byte>(result.GetBytes(), (int)result.GetLength().Bytes);
        return ShaderModuleExtensions.FromSpirv(device, spirv, null);
    }
}
