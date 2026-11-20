using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace PlatzAtlas.Studio;

internal sealed class StudioDiagnostics
{
    private readonly DateTime _started;
    private readonly string? _path;

    public StudioDiagnostics(string? path)
    {
        using var process = Process.GetCurrentProcess();
        _started = process.StartTime.ToUniversalTime();
        if (path is null)
            return;

        _path = Path.GetFullPath(path);
        using var log = new FileStream(_path, FileMode.CreateNew, FileAccess.Write);
    }

    public bool Enabled => _path is not null;

    public void Log(string eventName, object data)
    {
        if (_path is null)
            return;

        var line = JsonSerializer.Serialize(new
        {
            @event = eventName,
            processElapsedMs = (DateTime.UtcNow - _started).TotalMilliseconds,
            data
        });
        try
        {
            File.AppendAllText(_path, line + Environment.NewLine);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"Could not write Studio diagnostics: {error.Message}");
        }
    }
}
