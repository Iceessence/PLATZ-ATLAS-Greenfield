using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace PlatzAtlas.Rendering;

public sealed unsafe partial class VulkanViewport
{
    private Buffer _vertices;
    private DeviceMemory _vertexMemory;
    private GpuSceneVertex* _mappedVertices;
    private int _vertexCapacity;

    private Buffer _readbackBuffer;
    private DeviceMemory _readbackMemory;
    private byte* _mappedReadback;
    private ulong _readbackCapacity;

    private void CreatePipeline()
    {
        EnsureTextureDescriptors();
        var imageLayout = _imageLayout;
        var layout = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &imageLayout
        };
        Check(_vk.CreatePipelineLayout(_device, &layout, null, out _pipelineLayout), "create pipeline layout");

        ShaderModule vertex = default;
        ShaderModule fragment = default;
        try
        {
            vertex = LoadShader("Viewport.vert.spv");
            fragment = LoadShader("Viewport.frag.spv");
            fixed (byte* main = "main\0"u8)
                CreateGraphicsPipeline(vertex, fragment, main);
        }
        finally
        {
            if (fragment.Handle != 0)
                _vk.DestroyShaderModule(_device, fragment, null);
            if (vertex.Handle != 0)
                _vk.DestroyShaderModule(_device, vertex, null);
        }
    }

    private ShaderModule LoadShader(string name)
    {
        using Stream input = typeof(VulkanViewport).Assembly.GetManifestResourceStream($"PlatzAtlas.Rendering.Shaders.{name}")
            ?? throw new VulkanViewportException($"The embedded Vulkan shader {name} is missing.");
        var code = new uint[checked((int)input.Length / sizeof(uint))];
        input.ReadExactly(MemoryMarshal.AsBytes(code.AsSpan()));
        fixed (uint* first = code)
        {
            var create = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)input.Length,
                PCode = first
            };
            Check(_vk.CreateShaderModule(_device, &create, null, out var module), "create shader module");
            return module;
        }
    }

    private void CreateGraphicsPipeline(ShaderModule vertex, ShaderModule fragment, byte* main)
    {
        PipelineShaderStageCreateInfo* stages = stackalloc PipelineShaderStageCreateInfo[2]
        {
            new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vertex, PName = main },
            new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fragment, PName = main }
        };
        var binding = new VertexInputBindingDescription(0, (uint)sizeof(GpuSceneVertex), VertexInputRate.Vertex);
        VertexInputAttributeDescription* attributes = stackalloc VertexInputAttributeDescription[4]
        {
            new(0, 0, Format.R32G32Sfloat, 0),
            new(1, 0, Format.R32G32B32Sfloat, 2 * sizeof(float)),
            new(2, 0, Format.R32G32Sfloat, 5 * sizeof(float)),
            new(3, 0, Format.R32G32B32Sfloat, 7 * sizeof(float))
        };
        var vertexInput = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = 1,
            PVertexBindingDescriptions = &binding,
            VertexAttributeDescriptionCount = 4,
            PVertexAttributeDescriptions = attributes
        };
        var inputAssembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = PrimitiveTopology.TriangleList
        };
        var viewportState = new PipelineViewportStateCreateInfo
        {
            SType = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1,
            ScissorCount = 1
        };
        var rasterState = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            PolygonMode = PolygonMode.Fill,
            CullMode = CullModeFlags.None,
            FrontFace = FrontFace.CounterClockwise,
            LineWidth = 1f
        };
        var sampleState = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = SampleCountFlags.Count1Bit
        };
        var colorAttachment = new PipelineColorBlendAttachmentState
        {
            BlendEnable = true,
            SrcColorBlendFactor = BlendFactor.SrcAlpha,
            DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
            ColorBlendOp = BlendOp.Add,
            SrcAlphaBlendFactor = BlendFactor.One,
            DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
            AlphaBlendOp = BlendOp.Add,
            ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit
        };
        var blend = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            AttachmentCount = 1,
            PAttachments = &colorAttachment
        };
        DynamicState* dynamicStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
        var dynamicState = new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 2,
            PDynamicStates = dynamicStates
        };
        var request = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            StageCount = 2,
            PStages = stages,
            PVertexInputState = &vertexInput,
            PInputAssemblyState = &inputAssembly,
            PViewportState = &viewportState,
            PRasterizationState = &rasterState,
            PMultisampleState = &sampleState,
            PColorBlendState = &blend,
            PDynamicState = &dynamicState,
            Layout = _pipelineLayout,
            RenderPass = _renderPass,
            BasePipelineIndex = -1
        };
        Pipeline pipeline = default;
        Result created = _vk.CreateGraphicsPipelines(_device, default, 1, &request, null, &pipeline);
        _pipeline = pipeline;
        Check(created, "create graphics pipeline");
    }

    private bool TryPresent(RenderSnapshot snapshot, Span<byte> bgraReadback)
    {
        uint vertexCount = UploadVertices(snapshot);
        bool captureReadback = !bgraReadback.IsEmpty;
        ulong requiredReadbackBytes = captureReadback ? (ulong)_extent.Width * _extent.Height * 4UL : 0UL;
        if (captureReadback && requiredReadbackBytes > _readbackCapacity)
            CreateReadbackBuffer(requiredReadbackBytes);

        uint image = 0;
        Result acquire = _swapchainApi.AcquireNextImage(_device, _swapchain, AcquireTimeout, _imageAcquired, default, &image);
        if (acquire == Result.ErrorOutOfDateKhr)
            return false;
        if (acquire != Result.SuboptimalKhr)
            Check(acquire, "acquire a swapchain image");

        RecordDraw(snapshot.Background, image, vertexCount, captureReadback);
        CommandBuffer commandBuffer = _commands;
        Semaphore acquired = _imageAcquired;
        Semaphore drawn = _renderComplete[image];
        PipelineStageFlags wait = PipelineStageFlags.ColorAttachmentOutputBit;
        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &acquired,
            PWaitDstStageMask = &wait,
            CommandBufferCount = 1,
            PCommandBuffers = &commandBuffer,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &drawn
        };
        Fence fence = _drawComplete;
        Check(_vk.ResetFences(_device, 1, &fence), "reset the draw fence");
        Check(_vk.QueueSubmit(_queue, 1, &submit, fence), "submit a frame");

        // VK_KHR_swapchain releases the presentation semaphore when its image is acquired again.
        // Each image has its own semaphore so a draw fence alone never controls its reuse.
        SwapchainKHR swapchain = _swapchain;
        var present = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &drawn,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &image
        };
        Result presented = _swapchainApi.QueuePresent(_queue, &present);
        Check(_vk.WaitForFences(_device, 1, &fence, true, SubmitTimeout), "wait for the submitted frame");
        if (presented == Result.ErrorOutOfDateKhr)
            return false;
        if (presented != Result.SuboptimalKhr)
            Check(presented, "present a frame to the window");

        if (captureReadback)
            CopyReadbackPixels(bgraReadback, checked((int)requiredReadbackBytes));
        return true;
    }

    private void RecordDraw(RenderColor background, uint image, uint vertexCount, bool captureReadback)
    {
        Check(_vk.ResetCommandBuffer(_commands, 0), "reset the draw commands");
        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit
        };
        Check(_vk.BeginCommandBuffer(_commands, &begin), "begin draw commands");
        if (_timestamps.Handle != 0)
        {
            _vk.CmdResetQueryPool(_commands, _timestamps, 0, 2);
            _vk.CmdWriteTimestamp(_commands, PipelineStageFlags.ColorAttachmentOutputBit, _timestamps, 0);
        }

        RenderColor mapped = AttachmentColor(background);
        var clear = new ClearValue(new ClearColorValue(mapped.R, mapped.G, mapped.B, 1f));
        var area = new Rect2D(new Offset2D(0, 0), _extent);
        var pass = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass,
            Framebuffer = _framebuffers[image],
            RenderArea = area,
            ClearValueCount = 1,
            PClearValues = &clear
        };
        _vk.CmdBeginRenderPass(_commands, &pass, SubpassContents.Inline);
        if (vertexCount > 0)
        {
            var viewport = new Viewport(0, 0, _extent.Width, _extent.Height, 0, 1);
            _vk.CmdSetViewport(_commands, 0, 1, &viewport);
            _vk.CmdSetScissor(_commands, 0, 1, &area);
            _vk.CmdBindPipeline(_commands, PipelineBindPoint.Graphics, _pipeline);
            var imageSet = _imageSet;
            _vk.CmdBindDescriptorSets(_commands, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &imageSet, 0, null);
            Buffer vertices = _vertices;
            ulong offset = 0;
            _vk.CmdBindVertexBuffers(_commands, 0, 1, &vertices, &offset);
            _vk.CmdDraw(_commands, vertexCount, 1, 0, 0);
        }
        _vk.CmdEndRenderPass(_commands);

        if (captureReadback)
            RecordReadbackCopy(_swapchainImages[image]);

        if (_timestamps.Handle != 0)
            _vk.CmdWriteTimestamp(_commands, PipelineStageFlags.BottomOfPipeBit, _timestamps, 1);
        Check(_vk.EndCommandBuffer(_commands), "finish draw commands");
    }

    private void RecordReadbackCopy(Image swapchainImage)
    {
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        var toTransfer = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstAccessMask = AccessFlags.TransferReadBit,
            OldLayout = ImageLayout.PresentSrcKhr,
            NewLayout = ImageLayout.TransferSrcOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = swapchainImage,
            SubresourceRange = range
        };
        _vk.CmdPipelineBarrier(_commands, PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.TransferBit,
            0, 0, null, 0, null, 1, &toTransfer);

        var region = new BufferImageCopy(0, 0, 0,
            new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            new Offset3D(0, 0, 0),
            new Extent3D(_extent.Width, _extent.Height, 1));
        _vk.CmdCopyImageToBuffer(_commands, swapchainImage, ImageLayout.TransferSrcOptimal, _readbackBuffer, 1, &region);

        var hostFlush = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.HostReadBit
        };
        var toPresent = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = AccessFlags.TransferReadBit,
            DstAccessMask = 0,
            OldLayout = ImageLayout.TransferSrcOptimal,
            NewLayout = ImageLayout.PresentSrcKhr,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = swapchainImage,
            SubresourceRange = range
        };
        _vk.CmdPipelineBarrier(_commands, PipelineStageFlags.TransferBit,
            PipelineStageFlags.HostBit | PipelineStageFlags.BottomOfPipeBit,
            0, 1, &hostFlush, 0, null, 1, &toPresent);
    }

    private void CopyReadbackPixels(Span<byte> destination, int byteCount)
    {
        var source = new ReadOnlySpan<byte>(_mappedReadback, byteCount);
        if (_surfaceFormat.Format is Format.R8G8B8A8Unorm or Format.R8G8B8A8Srgb)
        {
            for (int i = 0; i < byteCount; i += 4)
            {
                destination[i] = source[i + 2];
                destination[i + 1] = source[i + 1];
                destination[i + 2] = source[i];
                destination[i + 3] = source[i + 3];
            }
            return;
        }

        source.CopyTo(destination);
    }

    private double? ReadGpuTime()
    {
        if (_timestamps.Handle == 0)
            return null;

        ulong* samples = stackalloc ulong[2];
        Result result = _vk.GetQueryPoolResults(_device, _timestamps, 0, 2, 2 * sizeof(ulong), samples, sizeof(ulong), QueryResultFlags.Result64Bit);
        if (result == Result.NotReady)
            return null;
        Check(result, "read GPU frame timestamps");

        ulong ticks = unchecked(samples[1] - samples[0]);
        if (_timestampBits < 64)
            ticks &= (1UL << (int)_timestampBits) - 1;
        return ticks * (double)_timestampPeriod / 1_000_000.0;
    }

    private uint UploadVertices(RenderSnapshot snapshot)
    {
        if (_imageStrip.Update(snapshot) || _imageView.Handle == 0)
            UploadImageStrip();
        int vertexCount = GpuImageStrip.VertexCount(snapshot);
        if (vertexCount > _vertexCapacity)
            CreateVertexBuffer(Math.Max(vertexCount, 64));

        _imageStrip.Fill(snapshot, new Span<GpuSceneVertex>(_mappedVertices, vertexCount), AttachmentColor, UsesSrgbAttachment);
        return (uint)vertexCount;
    }

    private void CreateVertexBuffer(int capacity)
    {
        // Present waits for the graphics fence before returning, so no commands reference the old buffer.
        DestroyVertexBuffer();
        var create = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = (ulong)capacity * (uint)sizeof(GpuSceneVertex),
            Usage = BufferUsageFlags.VertexBufferBit,
            SharingMode = SharingMode.Exclusive
        };
        Check(_vk.CreateBuffer(_device, &create, null, out _vertices), "create the vertex buffer");
        _vk.GetBufferMemoryRequirements(_device, _vertices, out var requirements);
        var allocate = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = FindVertexMemoryType(requirements.MemoryTypeBits)
        };
        Check(_vk.AllocateMemory(_device, &allocate, null, out _vertexMemory), "allocate vertex memory");
        Check(_vk.BindBufferMemory(_device, _vertices, _vertexMemory, 0), "bind vertex memory");
        void* mapped = null;
        Check(_vk.MapMemory(_device, _vertexMemory, 0, allocate.AllocationSize, 0, &mapped), "map vertex memory");
        _mappedVertices = (GpuSceneVertex*)mapped;
        _vertexCapacity = capacity;
    }

    private void CreateReadbackBuffer(ulong capacity)
    {
        DestroyReadbackBuffer();
        var create = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = capacity,
            Usage = BufferUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive
        };
        Check(_vk.CreateBuffer(_device, &create, null, out _readbackBuffer), "create the readback buffer");
        _vk.GetBufferMemoryRequirements(_device, _readbackBuffer, out var requirements);
        var allocate = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = FindVertexMemoryType(requirements.MemoryTypeBits)
        };
        Check(_vk.AllocateMemory(_device, &allocate, null, out _readbackMemory), "allocate readback memory");
        Check(_vk.BindBufferMemory(_device, _readbackBuffer, _readbackMemory, 0), "bind readback memory");
        void* mapped = null;
        Check(_vk.MapMemory(_device, _readbackMemory, 0, allocate.AllocationSize, 0, &mapped), "map readback memory");
        _mappedReadback = (byte*)mapped;
        _readbackCapacity = capacity;
    }

    private uint FindVertexMemoryType(uint compatibleTypes)
    {
        const MemoryPropertyFlags required = MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit;
        _vk.GetPhysicalDeviceMemoryProperties(_adapter, out var memory);
        for (int i = 0; i < memory.MemoryTypeCount; i++)
        {
            if ((compatibleTypes & (1u << i)) != 0 && memory.MemoryTypes[i].PropertyFlags.HasFlag(required))
                return (uint)i;
        }
        throw new VulkanViewportException("Vulkan has no host-visible, coherent memory for the first viewport profile.");
    }

    private void DestroyVertexBuffer()
    {
        if (_mappedVertices != null)
            _vk.UnmapMemory(_device, _vertexMemory);
        if (_vertices.Handle != 0)
            _vk.DestroyBuffer(_device, _vertices, null);
        if (_vertexMemory.Handle != 0)
            _vk.FreeMemory(_device, _vertexMemory, null);
        _mappedVertices = null;
        _vertices = default;
        _vertexMemory = default;
        _vertexCapacity = 0;
    }

    private void DestroyReadbackBuffer()
    {
        if (_mappedReadback != null)
            _vk.UnmapMemory(_device, _readbackMemory);
        if (_readbackBuffer.Handle != 0)
            _vk.DestroyBuffer(_device, _readbackBuffer, null);
        if (_readbackMemory.Handle != 0)
            _vk.FreeMemory(_device, _readbackMemory, null);
        _mappedReadback = null;
        _readbackBuffer = default;
        _readbackMemory = default;
        _readbackCapacity = 0;
    }

    private RenderColor AttachmentColor(RenderColor color) => UsesSrgbAttachment
        ? new RenderColor(ToLinear(color.R), ToLinear(color.G), ToLinear(color.B))
        : color;

    private static float ToLinear(float channel) => channel <= .04045f
        ? channel / 12.92f
        : MathF.Pow((channel + .055f) / 1.055f, 2.4f);
}
