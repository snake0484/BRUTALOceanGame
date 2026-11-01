using System.Buffers.Binary;
using Brutal.Logging;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

namespace BrutalGame.Engine.Rendering;

/// <summary>
/// Driver pipeline cache, loaded from and saved to a file next to the exe. A file from another GPU or driver is
/// ignored. Every pipeline is created through it; creation feedback counts cache hits and driver time.
/// </summary>
public sealed unsafe class PipelineCache : IDisposable
{
    private const string FileName = "pipeline_cache.bin";
    private const int HeaderSize = 32;

    private static readonly LogCategory Log = LogCategory.Make("PipelineCache");

    public readonly VkPipelineCache Handle;
    private readonly DeviceEx _device;
    private readonly string _path;
    private readonly int _loadedBytes;

    public int Created { get; private set; }
    public int Hits { get; private set; }
    public double DriverMs { get; private set; }

    public PipelineCache(DeviceEx device, VkPhysicalDeviceProperties properties)
    {
        _device = device;
        _path = Path.Combine(AppContext.BaseDirectory, FileName);

        Span<byte> header = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteInt32LittleEndian(header, HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], (int)VkPipelineCacheHeaderVersion.One);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], properties.VendorID);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], properties.DeviceID);
        new ReadOnlySpan<byte>(properties.PipelineCacheUUID, 16).CopyTo(header[16..]);

        var data = Load(_path, header);
        _loadedBytes = data.Length;

        fixed (byte* initial = data)
        {
            Handle = device.CreatePipelineCache(
                new VkPipelineCacheCreateInfo { InitialDataSize = (nuint)data.Length, InitialData = initial },
                null
            );
        }
    }

    private static byte[] Load(string path, ReadOnlySpan<byte> header)
    {
        if (!File.Exists(path))
        {
            Log.Info($"no cache at {path}, starting empty");
            return [];
        }

        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"could not read {path}: {e.Message}");
            return [];
        }

        if (data.Length < HeaderSize || !data.AsSpan(0, HeaderSize).SequenceEqual(header))
        {
            Log.Warning($"ignoring {path}: header does not match this GPU and driver");
            return [];
        }

        Log.Info($"loaded {data.Length} bytes from {path}");
        return data;
    }

    public VkPipeline CreateGraphicsPipeline(VkGraphicsPipelineCreateInfo info)
    {
        var feedback = new VkPipelineCreationFeedback();
        var feedbackInfo = new VkPipelineCreationFeedbackCreateInfo { Next = info.Next, PipelineCreationFeedback = &feedback };
        info.Next = &feedbackInfo;
        var pipeline = _device.CreateGraphicsPipeline(Handle, info, null);
        Record(feedback);
        return pipeline;
    }

    public VkPipeline CreateComputePipeline(VkComputePipelineCreateInfo info)
    {
        var feedback = new VkPipelineCreationFeedback();
        var feedbackInfo = new VkPipelineCreationFeedbackCreateInfo { Next = info.Next, PipelineCreationFeedback = &feedback };
        info.Next = &feedbackInfo;
        var pipeline = _device.CreateComputePipeline(Handle, info, null);
        Record(feedback);
        return pipeline;
    }

    private void Record(VkPipelineCreationFeedback feedback)
    {
        Created++;

        if ((feedback.Flags & VkPipelineCreationFeedbackFlags.ValidBit) == 0)
            return;

        if ((feedback.Flags & VkPipelineCreationFeedbackFlags.ApplicationPipelineCacheHitBit) != 0)
            Hits++;

        DriverMs += feedback.Duration / 1e6;
    }

    private void Save()
    {
        var data = new byte[(int)_device.GetPipelineCacheData_Count(Handle)];
        if (_device.GetPipelineCacheData_Write(Handle, data) != VkResult.Success)
        {
            Log.Warning("could not read the pipeline cache back from the driver; not saved");
            return;
        }

        try
        {
            var temp = _path + ".tmp";
            File.WriteAllBytes(temp, data);
            File.Move(temp, _path, true);
            Log.Info($"{Created} pipelines, {Hits} cache hits, {DriverMs:F1} ms in the driver; saved {data.Length} bytes (loaded {_loadedBytes}) to {_path}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"could not write {_path}: {e.Message}");
        }
    }

    /// <summary>Saves the cache file, then destroys the cache. Call after the device is idle.</summary>
    public void Dispose()
    {
        Save();
        _device.DestroyPipelineCache(Handle, null);
    }
}
