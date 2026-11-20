using System.Diagnostics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace PlatzAtlas.Rendering;

public readonly record struct VulkanFrame(int Width, int Height, long FrameNumber, double ElapsedMilliseconds)
{
    /// <summary>GPU time from color attachment availability through render completion, when supported.</summary>
    public double? GpuMilliseconds { get; init; }
}

public sealed class VulkanViewportException(string message, Exception? cause = null) : Exception(message, cause);

/// <summary>A single-threaded, on-demand Vulkan renderer. The caller retains ownership of the viewport HWND.</summary>
public sealed unsafe partial class VulkanViewport : IDisposable
{
    private const ulong AcquireTimeout = 500_000_000;
    private const ulong SubmitTimeout = 2_000_000_000;
    private readonly Vk _vk;
    private KhrSurface _surfaceApi = null!;
    private KhrSwapchain _swapchainApi = null!;
    private Instance _instance;
    private SurfaceKHR _surface;
    private PhysicalDevice _adapter;
    private Device _device;
    private Queue _queue;
    private uint _queueFamily;
    private CommandPool _pool;
    private CommandBuffer _commands;
    private Semaphore _imageAcquired;
    private Fence _drawComplete;
    private QueryPool _timestamps;
    private uint _timestampBits;
    private float _timestampPeriod;
    private bool _simulateDeviceLoss;
    private bool _disposed;
    private long _frames;
    private int _requestedWidth;
    private int _requestedHeight;

    private string _deviceDiagnostics = "";
    public string DeviceName { get; private set; } = "";
    public string Diagnostics => _swapchain.Handle == 0
        ? _deviceDiagnostics
        : $"{_deviceDiagnostics}; {_surfaceFormat.Format}";

    public VulkanViewport(nint windowHandle)
    {
        ArgumentOutOfRangeException.ThrowIfZero(windowHandle);
        try
        {
            _vk = Vk.GetApi();
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or NotSupportedException)
        {
            throw new VulkanViewportException("The system Vulkan loader could not start.", ex);
        }

        try
        {
            CreateInstanceAndSurface(windowHandle);
            SelectAdapter();
            CreateDevice();
            CreateFrameResources();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void SimulateDeviceLoss()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _simulateDeviceLoss = true;
    }

    public VulkanFrame Present(RenderSnapshot snapshot, int pixelWidth, int pixelHeight)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelHeight);
        if (_simulateDeviceLoss)
            throw new VulkanViewportException("Vulkan device lost (simulated).");

        long started = Stopwatch.GetTimestamp();
        if (_swapchain.Handle == 0 || _requestedWidth != pixelWidth || _requestedHeight != pixelHeight)
            RebuildSwapchain(pixelWidth, pixelHeight);

        // Win32 can change the client extent between querying the surface and acquiring the image.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (TryPresent(snapshot))
            {
                double? gpuMilliseconds = ReadGpuTime();
                return new((int)_extent.Width, (int)_extent.Height, ++_frames, Stopwatch.GetElapsedTime(started).TotalMilliseconds)
                {
                    GpuMilliseconds = gpuMilliseconds
                };
            }

            if (attempt == 0)
                RebuildSwapchain(pixelWidth, pixelHeight);
        }

        throw new VulkanViewportException("The Vulkan surface changed during both presentation attempts.");
    }

    private void CreateInstanceAndSurface(nint windowHandle)
    {
        bool validationAvailable = HasValidationLayer();
        bool validationRequested = Environment.GetEnvironmentVariable("PLATZ_ATLAS_VK_VALIDATION") == "1";
        bool enableValidation = validationRequested && validationAvailable;

        fixed (byte* surfaceName = "VK_KHR_surface\0"u8)
        fixed (byte* win32Name = "VK_KHR_win32_surface\0"u8)
        fixed (byte* appName = "PLATZ ATLAS\0"u8)
        fixed (byte* validationName = "VK_LAYER_KHRONOS_validation\0"u8)
        {
            byte** extensions = stackalloc byte*[2] { surfaceName, win32Name };
            var application = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = appName,
                ApiVersion = Vk.Version10
            };
            var request = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &application,
                EnabledExtensionCount = 2,
                PpEnabledExtensionNames = extensions,
                EnabledLayerCount = enableValidation ? 1u : 0u,
                PpEnabledLayerNames = enableValidation ? &validationName : null
            };
            Check(_vk.CreateInstance(&request, null, out _instance), "create instance");
        }

        if (!_vk.TryGetInstanceExtension(_instance, out _surfaceApi)
            || !_vk.TryGetInstanceExtension<KhrWin32Surface>(_instance, out var win32))
            throw new VulkanViewportException("Vulkan Win32 surface support is unavailable.");

        using (win32)
        {
            var request = new Win32SurfaceCreateInfoKHR
            {
                SType = StructureType.Win32SurfaceCreateInfoKhr,
                Hwnd = windowHandle,
                Hinstance = GetModuleHandle(null)
            };
            Check(win32.CreateWin32Surface(_instance, &request, null, out _surface), "create Win32 surface");
        }

        _deviceDiagnostics = enableValidation ? "Khronos validation enabled"
            : validationAvailable ? "Khronos validation available (set PLATZ_ATLAS_VK_VALIDATION=1)"
            : "Khronos validation layer unavailable";
    }

    private bool HasValidationLayer()
    {
        uint count = 0;
        Check(_vk.EnumerateInstanceLayerProperties(&count, null), "enumerate instance layers");
        var layers = new LayerProperties[count];
        fixed (LayerProperties* first = layers)
        {
            Check(_vk.EnumerateInstanceLayerProperties(&count, first), "read instance layers");
            for (int i = 0; i < count; i++)
            {
                if (Marshal.PtrToStringUTF8((nint)first[i].LayerName) == "VK_LAYER_KHRONOS_validation")
                    return true;
            }
        }
        return false;
    }

    private void SelectAdapter()
    {
        uint count = 0;
        Check(_vk.EnumeratePhysicalDevices(_instance, &count, null), "enumerate adapters");
        var adapters = new PhysicalDevice[count];
        fixed (PhysicalDevice* first = adapters)
            Check(_vk.EnumeratePhysicalDevices(_instance, &count, first), "read adapters");

        string validation = _deviceDiagnostics;
        foreach (PhysicalDevice adapter in adapters)
        {
            if (!SupportsSwapchain(adapter) || !TryGetQueue(adapter, out uint queue, out uint timestampBits))
                continue;

            _vk.GetPhysicalDeviceProperties(adapter, out PhysicalDeviceProperties properties);
            bool discrete = properties.DeviceType == PhysicalDeviceType.DiscreteGpu;
            if (_adapter.Handle != 0 && !discrete)
                continue;

            _adapter = adapter;
            _queueFamily = queue;
            _timestampBits = timestampBits;
            _timestampPeriod = properties.Limits.TimestampPeriod;
            DeviceName = Marshal.PtrToStringUTF8((nint)properties.DeviceName) ?? "Vulkan adapter";
            uint driver = properties.DriverVersion;
            string driverVersion = properties.VendorID == 0x10de
                ? $"{driver >> 22}.{(driver >> 14) & 0xff}.{(driver >> 6) & 0xff}.{driver & 0x3f}"
                : $"0x{driver:x8}";
            uint api = properties.ApiVersion;
            string apiVersion = $"{(api >> 22) & 0x7f}.{(api >> 12) & 0x3ff}.{api & 0xfff}";
            _deviceDiagnostics = $"{DeviceName}; Vulkan {apiVersion}; driver {driverVersion}; FIFO; timestamp bits {timestampBits}; {validation}";
            if (discrete)
                break;
        }

        if (_adapter.Handle == 0)
            throw new VulkanViewportException("No Vulkan adapter has a graphics queue that can present to this window.");
    }

    private bool SupportsSwapchain(PhysicalDevice adapter)
    {
        uint count = 0;
        Check(_vk.EnumerateDeviceExtensionProperties(adapter, (byte*)null, &count, null), "enumerate device extensions");
        var extensions = new ExtensionProperties[count];
        fixed (ExtensionProperties* first = extensions)
        {
            Check(_vk.EnumerateDeviceExtensionProperties(adapter, (byte*)null, &count, first), "read device extensions");
            for (int i = 0; i < count; i++)
            {
                if (Marshal.PtrToStringUTF8((nint)first[i].ExtensionName) == "VK_KHR_swapchain")
                    return true;
            }
        }
        return false;
    }

    private bool TryGetQueue(PhysicalDevice adapter, out uint queue, out uint timestampBits)
    {
        uint count = 0;
        _vk.GetPhysicalDeviceQueueFamilyProperties(adapter, &count, null);
        var queues = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* first = queues)
            _vk.GetPhysicalDeviceQueueFamilyProperties(adapter, &count, first);

        for (uint i = 0; i < count; i++)
        {
            if (!queues[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit))
                continue;

            Check(_surfaceApi.GetPhysicalDeviceSurfaceSupport(adapter, i, _surface, out var supportsSurface), "query presentation support");
            if (supportsSurface)
            {
                queue = i;
                timestampBits = queues[i].TimestampValidBits;
                return true;
            }
        }

        queue = 0;
        timestampBits = 0;
        return false;
    }

    private void CreateDevice()
    {
        float priority = 1f;
        var queue = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = _queueFamily,
            QueueCount = 1,
            PQueuePriorities = &priority
        };

        fixed (byte* extension = "VK_KHR_swapchain\0"u8)
        {
            byte* extensionName = extension;
            var request = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &queue,
                EnabledExtensionCount = 1,
                PpEnabledExtensionNames = &extensionName
            };
            Check(_vk.CreateDevice(_adapter, &request, null, out _device), "create device");
        }

        if (!_vk.TryGetDeviceExtension(_instance, _device, out _swapchainApi))
            throw new VulkanViewportException("The Vulkan swapchain entry points could not load.");

        _vk.GetDeviceQueue(_device, _queueFamily, 0, out _queue);
    }

    private void CreateFrameResources()
    {
        var pool = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            QueueFamilyIndex = _queueFamily,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit
        };
        Check(_vk.CreateCommandPool(_device, &pool, null, out _pool), "create command pool");

        var commands = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _pool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1
        };
        Check(_vk.AllocateCommandBuffers(_device, &commands, out _commands), "allocate command buffer");

        var semaphore = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        Check(_vk.CreateSemaphore(_device, &semaphore, null, out _imageAcquired), "create image-acquired semaphore");

        var fence = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
        Check(_vk.CreateFence(_device, &fence, null, out _drawComplete), "create draw fence");

        if (_timestampBits > 0)
        {
            var timestamps = new QueryPoolCreateInfo
            {
                SType = StructureType.QueryPoolCreateInfo,
                QueryType = QueryType.Timestamp,
                QueryCount = 2
            };
            Check(_vk.CreateQueryPool(_device, &timestamps, null, out _timestamps), "create timestamp pool");
        }
    }

    private static void Check(Result result, string action)
    {
        if (result != Result.Success)
            throw new VulkanViewportException($"Vulkan could not {action} ({result}).");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_device.Handle != 0)
        {
            // A lost device still owns its handles. Allow destruction even when the wait reports a loss.
            _vk.DeviceWaitIdle(_device);
            DestroySwapchain();
            DestroyVertexBuffer();
            if (_timestamps.Handle != 0)
                _vk.DestroyQueryPool(_device, _timestamps, null);
            if (_drawComplete.Handle != 0)
                _vk.DestroyFence(_device, _drawComplete, null);
            if (_imageAcquired.Handle != 0)
                _vk.DestroySemaphore(_device, _imageAcquired, null);
            if (_pool.Handle != 0)
                _vk.DestroyCommandPool(_device, _pool, null);
            _vk.DestroyDevice(_device, null);
        }

        if (_surface.Handle != 0)
            _surfaceApi.DestroySurface(_instance, _surface, null);
        if (_instance.Handle != 0)
            _vk.DestroyInstance(_instance, null);

        _swapchainApi?.Dispose();
        _surfaceApi?.Dispose();
        _vk.Dispose();
    }

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
