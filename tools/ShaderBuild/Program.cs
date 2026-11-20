using System.Runtime.InteropServices;
using Silk.NET.Shaderc;

if (args is not [var shaderDirectory])
{
    Console.Error.WriteLine("Pass the directory that contains Viewport.vert and Viewport.frag.");
    return 2;
}

unsafe
{
    using Shaderc shaderc = Shaderc.GetApi();
    Compiler* compiler = shaderc.CompilerInitialize();
    if (compiler == null)
        throw new InvalidOperationException("Shaderc could not initialize.");

    try
    {
        Compile(shaderc, compiler, shaderDirectory, "Viewport.vert", ShaderKind.VertexShader);
        Compile(shaderc, compiler, shaderDirectory, "Viewport.frag", ShaderKind.FragmentShader);
    }
    finally
    {
        shaderc.CompilerRelease(compiler);
    }
}

return 0;

static unsafe void Compile(Shaderc shaderc, Compiler* compiler, string directory, string name, ShaderKind stage)
{
    string input = Path.Combine(directory, name);
    byte[] source = File.ReadAllBytes(input);
    fixed (byte* sourcePointer = source)
    {
        CompilationResult* result = shaderc.CompileIntoSpv(compiler, sourcePointer, (nuint)source.Length,
            stage, name, "main", null);
        if (result == null)
            throw new InvalidOperationException($"Shaderc returned no result for {name}.");

        try
        {
            if (shaderc.ResultGetCompilationStatus(result) != CompilationStatus.Success)
                throw new InvalidOperationException(Marshal.PtrToStringUTF8((nint)shaderc.ResultGetErrorMessage(result)));

            using var output = File.Create(input + ".spv");
            output.Write(new ReadOnlySpan<byte>(shaderc.ResultGetBytes(result), checked((int)shaderc.ResultGetLength(result))));
        }
        finally
        {
            shaderc.ResultRelease(result);
        }
    }
}
