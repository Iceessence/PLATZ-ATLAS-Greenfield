namespace PlatzAtlas.Rendering;

public static class ViewportArguments
{
    public static string? ReadOption(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        if (index < 0)
            return null;
        if (index + 1 == args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Missing value after {flag}.");
        return args[index + 1];
    }

    public static RendererPreference ParseRenderer(string[] args)
    {
        if (ReadOption(args, "--renderer") is { } value)
        {
            return value.ToLowerInvariant() switch
            {
                "auto" => RendererPreference.Auto,
                "vulkan" => RendererPreference.Vulkan,
                "opengl" or "gl" => RendererPreference.OpenGl,
                "cpu" or "cpureference" or "cpu-reference" => RendererPreference.CpuReference,
                _ => throw new ArgumentException($"Unsupported renderer preference \"{value}\". Use auto, vulkan, opengl, or cpu.")
            };
        }

        if (args.Contains("--cpu"))
            return RendererPreference.CpuReference;
        if (args.Contains("--opengl"))
            return RendererPreference.OpenGl;
        if (args.Contains("--vulkan"))
            return RendererPreference.Vulkan;
        return RendererPreference.Auto;
    }
}
