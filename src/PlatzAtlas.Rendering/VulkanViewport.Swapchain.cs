using Silk.NET.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace PlatzAtlas.Rendering;

public sealed unsafe partial class VulkanViewport
{
    private SwapchainKHR _swapchain;
    private Extent2D _extent;
    private SurfaceFormatKHR _surfaceFormat;
    private ImageView[] _imageViews = [];
    private Framebuffer[] _framebuffers = [];
    private Semaphore[] _renderComplete = [];
    private RenderPass _renderPass;
    private PipelineLayout _pipelineLayout;
    private Pipeline _pipeline;

    private bool UsesSrgbAttachment => _surfaceFormat.Format is Format.B8G8R8A8Srgb or Format.R8G8B8A8Srgb;

    private void RebuildSwapchain(int pixelWidth, int pixelHeight)
    {
        Check(_vk.DeviceWaitIdle(_device), "wait for the old swapchain");
        DestroySwapchain();

        Check(_surfaceApi.GetPhysicalDeviceSurfaceCapabilities(_adapter, _surface, out var capabilities), "read surface capabilities");
        if (!capabilities.SupportedUsageFlags.HasFlag(ImageUsageFlags.ColorAttachmentBit))
            throw new VulkanViewportException("The Vulkan surface cannot accept rendered color images.");

        _extent = capabilities.CurrentExtent.Width != uint.MaxValue ? capabilities.CurrentExtent : new Extent2D(
            Math.Clamp((uint)pixelWidth, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
            Math.Clamp((uint)pixelHeight, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height));
        if (_extent.Width == 0 || _extent.Height == 0)
            throw new VulkanViewportException("The Vulkan window surface has no drawable area.");

        _surfaceFormat = ChooseSurfaceFormat();
        uint desiredImages = capabilities.MinImageCount + 1;
        if (capabilities.MaxImageCount > 0)
            desiredImages = Math.Min(desiredImages, capabilities.MaxImageCount);

        var request = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = desiredImages,
            ImageFormat = _surfaceFormat.Format,
            ImageColorSpace = _surfaceFormat.ColorSpace,
            ImageExtent = _extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = capabilities.CurrentTransform,
            CompositeAlpha = ChooseCompositeAlpha(capabilities.SupportedCompositeAlpha),
            PresentMode = PresentModeKHR.FifoKhr,
            Clipped = true
        };
        Check(_swapchainApi.CreateSwapchain(_device, &request, null, out _swapchain), "create swapchain");
        CreateRenderPass();
        CreatePipeline();
        CreateImageResources();
        _requestedWidth = pixelWidth;
        _requestedHeight = pixelHeight;
    }

    private SurfaceFormatKHR ChooseSurfaceFormat()
    {
        uint count = 0;
        Check(_surfaceApi.GetPhysicalDeviceSurfaceFormats(_adapter, _surface, &count, null), "enumerate surface formats");
        var formats = new SurfaceFormatKHR[count];
        fixed (SurfaceFormatKHR* first = formats)
            Check(_surfaceApi.GetPhysicalDeviceSurfaceFormats(_adapter, _surface, &count, first), "read surface formats");

        Format[] preferred = [Format.B8G8R8A8Unorm, Format.R8G8B8A8Unorm, Format.B8G8R8A8Srgb, Format.R8G8B8A8Srgb];
        if (count == 1 && formats[0].Format == Format.Undefined)
            return new SurfaceFormatKHR(preferred[0], formats[0].ColorSpace);

        foreach (Format candidate in preferred)
        {
            foreach (SurfaceFormatKHR format in formats)
            {
                if (format.Format == candidate && format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                    return format;
            }
        }

        throw new VulkanViewportException("The Vulkan surface has no supported 8-bit color format.");
    }

    private static CompositeAlphaFlagsKHR ChooseCompositeAlpha(CompositeAlphaFlagsKHR supported)
    {
        // The spec requires at least one bit. Prefer bit zero (opaque); otherwise select the lowest set bit.
        uint mask = (uint)supported;
        return (CompositeAlphaFlagsKHR)(mask & (0u - mask));
    }

    private void CreateRenderPass()
    {
        var attachment = new AttachmentDescription
        {
            Format = _surfaceFormat.Format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.PresentSrcKhr
        };
        var color = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &color
        };
        var dependency = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit
        };
        var request = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &attachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency
        };
        Check(_vk.CreateRenderPass(_device, &request, null, out _renderPass), "create render pass");
    }

    private void CreateImageResources()
    {
        uint count = 0;
        Check(_swapchainApi.GetSwapchainImages(_device, _swapchain, &count, null), "enumerate swapchain images");
        var images = new Image[count];
        fixed (Image* first = images)
            Check(_swapchainApi.GetSwapchainImages(_device, _swapchain, &count, first), "read swapchain images");

        _imageViews = new ImageView[count];
        _framebuffers = new Framebuffer[count];
        _renderComplete = new Semaphore[count];
        for (int i = 0; i < count; i++)
        {
            var view = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = images[i],
                ViewType = ImageViewType.Type2D,
                Format = _surfaceFormat.Format,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1)
            };
            Check(_vk.CreateImageView(_device, &view, null, out _imageViews[i]), "create swapchain image view");

            ImageView attachment = _imageViews[i];
            var framebuffer = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _renderPass,
                AttachmentCount = 1,
                PAttachments = &attachment,
                Width = _extent.Width,
                Height = _extent.Height,
                Layers = 1
            };
            Check(_vk.CreateFramebuffer(_device, &framebuffer, null, out _framebuffers[i]), "create framebuffer");

            var semaphore = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
            Check(_vk.CreateSemaphore(_device, &semaphore, null, out _renderComplete[i]), "create per-image presentation semaphore");
        }
    }

    private void DestroySwapchain()
    {
        foreach (Semaphore semaphore in _renderComplete)
        {
            if (semaphore.Handle != 0)
                _vk.DestroySemaphore(_device, semaphore, null);
        }
        foreach (Framebuffer framebuffer in _framebuffers)
        {
            if (framebuffer.Handle != 0)
                _vk.DestroyFramebuffer(_device, framebuffer, null);
        }
        foreach (ImageView imageView in _imageViews)
        {
            if (imageView.Handle != 0)
                _vk.DestroyImageView(_device, imageView, null);
        }

        if (_pipeline.Handle != 0)
            _vk.DestroyPipeline(_device, _pipeline, null);
        if (_pipelineLayout.Handle != 0)
            _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
        if (_renderPass.Handle != 0)
            _vk.DestroyRenderPass(_device, _renderPass, null);
        if (_swapchain.Handle != 0)
            _swapchainApi.DestroySwapchain(_device, _swapchain, null);

        _renderComplete = [];
        _framebuffers = [];
        _imageViews = [];
        _pipeline = default;
        _pipelineLayout = default;
        _renderPass = default;
        _swapchain = default;
    }
}
