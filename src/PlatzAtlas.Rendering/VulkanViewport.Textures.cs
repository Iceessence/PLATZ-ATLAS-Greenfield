using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace PlatzAtlas.Rendering;

public sealed unsafe partial class VulkanViewport
{
    private readonly GpuImageStrip _imageStrip = new();
    private DescriptorSetLayout _imageLayout;
    private DescriptorPool _imagePool;
    private DescriptorSet _imageSet;
    private Buffer _imageBuffer;
    private BufferView _imageView;
    private DeviceMemory _imageMemory;
    private byte* _mappedImage;
    private int _imageCapacity;
    private uint _maxImageTexels;

    private void EnsureTextureDescriptors()
    {
        if (_imageLayout.Handle != 0)
            return;
        _vk.GetPhysicalDeviceProperties(_adapter, out var properties);
        _maxImageTexels = properties.Limits.MaxTexelBufferElements;
        _vk.GetPhysicalDeviceFormatProperties(_adapter, Format.R8G8B8A8Unorm, out var format);
        if ((format.BufferFeatures & FormatFeatureFlags.UniformTexelBufferBit) == 0)
            throw new VulkanViewportException("The Vulkan device does not support RGBA8 texture buffers.");

        const DescriptorType texture = DescriptorType.UniformTexelBuffer;
        var binding = new DescriptorSetLayoutBinding(0, texture, 1, ShaderStageFlags.FragmentBit);
        var layout = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 1, PBindings = &binding
        };
        Check(_vk.CreateDescriptorSetLayout(_device, &layout, null, out _imageLayout), "create texture layout");
        var size = new DescriptorPoolSize(texture, 1);
        var pool = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 1, PoolSizeCount = 1, PPoolSizes = &size
        };
        Check(_vk.CreateDescriptorPool(_device, &pool, null, out _imagePool), "create texture descriptor pool");
        var setLayout = _imageLayout;
        var allocate = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _imagePool, DescriptorSetCount = 1,
            PSetLayouts = &setLayout
        };
        var set = default(DescriptorSet);
        Check(_vk.AllocateDescriptorSets(_device, &allocate, &set), "allocate texture descriptor");
        _imageSet = set;
    }

    private void UploadImageStrip()
    {
        var bytes = _imageStrip.Bytes;
        if ((uint)bytes.Length / 4 > _maxImageTexels)
            throw new VulkanViewportException("The imported images exceed this GPU's texture buffer size.");
        if (bytes.Length > _imageCapacity)
            CreateTextureBuffer(bytes.Length);
        bytes.AsSpan().CopyTo(new Span<byte>(_mappedImage, bytes.Length));
    }

    private void CreateTextureBuffer(int capacity)
    {
        // The previous submitted graphics fence is complete before any frame upload.
        DestroyTextureBuffer();
        var create = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo, Size = (ulong)capacity,
            Usage = BufferUsageFlags.UniformTexelBufferBit, SharingMode = SharingMode.Exclusive
        };
        Check(_vk.CreateBuffer(_device, &create, null, out _imageBuffer), "create texture buffer");
        _vk.GetBufferMemoryRequirements(_device, _imageBuffer, out var requirements);
        var allocate = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo, AllocationSize = requirements.Size,
            MemoryTypeIndex = FindVertexMemoryType(requirements.MemoryTypeBits)
        };
        Check(_vk.AllocateMemory(_device, &allocate, null, out _imageMemory), "allocate texture memory");
        Check(_vk.BindBufferMemory(_device, _imageBuffer, _imageMemory, 0), "bind texture memory");
        void* mapped = null;
        Check(_vk.MapMemory(_device, _imageMemory, 0, requirements.Size, 0, &mapped), "map texture memory");
        _mappedImage = (byte*)mapped;
        var view = new BufferViewCreateInfo
        {
            SType = StructureType.BufferViewCreateInfo, Buffer = _imageBuffer,
            Format = Format.R8G8B8A8Unorm, Range = (ulong)capacity
        };
        Check(_vk.CreateBufferView(_device, &view, null, out _imageView), "create the RGBA8 texture view");
        var image = _imageView;
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstSet = _imageSet, DescriptorCount = 1,
            DescriptorType = DescriptorType.UniformTexelBuffer, PTexelBufferView = &image
        };
        _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);
        _imageCapacity = capacity;
    }

    private void DestroyTextureResources()
    {
        if (_imagePool.Handle != 0)
            _vk.DestroyDescriptorPool(_device, _imagePool, null);
        if (_imageLayout.Handle != 0)
            _vk.DestroyDescriptorSetLayout(_device, _imageLayout, null);
        DestroyTextureBuffer();
    }

    private void DestroyTextureBuffer()
    {
        if (_imageView.Handle != 0)
            _vk.DestroyBufferView(_device, _imageView, null);
        if (_mappedImage != null)
            _vk.UnmapMemory(_device, _imageMemory);
        if (_imageBuffer.Handle != 0)
            _vk.DestroyBuffer(_device, _imageBuffer, null);
        if (_imageMemory.Handle != 0)
            _vk.FreeMemory(_device, _imageMemory, null);
        _imageView = default;
        _mappedImage = null;
        _imageBuffer = default;
        _imageMemory = default;
        _imageCapacity = 0;
    }
}
