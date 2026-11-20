using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlatzAtlas.Core;

namespace PlatzAtlas.Runtime;

public sealed record RuntimeRoom(Guid ProjectId, Guid SceneId, long SourceRevision, string Name,
    IReadOnlyList<Lantern> Objects, IReadOnlyList<LightSwitch> Switches)
{
    public static RuntimeRoom FromProject(MuseumProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new RuntimeRoom(project.ProjectId, project.SceneId, project.Revision, project.Name,
            Array.AsReadOnly(project.Objects.ToArray()), Array.AsReadOnly(project.Switches.ToArray()));
    }
}

public static class RoomPackage
{
    public const string Extension = ".platzroom";
    public const string RuntimeProfile = "night-museum/1";
    private const int MaxBytes = 256 * 1024;
    private const int MaxRecords = 128;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12,
        Converters = { new JsonStringEnumConverter<SwitchBehavior>(JsonNamingPolicy.CamelCase, false) }
    };

    public static byte[] Encode(RuntimeRoom room)
    {
        Validate(room);
        var payload = JsonSerializer.SerializeToUtf8Bytes(room, Json);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteString("runtimeProfile", RuntimeProfile);
            writer.WriteString("sha256", Digest(payload));
            writer.WritePropertyName("room");
            writer.WriteRawValue(payload);
            writer.WriteEndObject();
        }
        if (buffer.Length > MaxBytes)
            throw new InvalidDataException("The room exceeds the package size limit.");
        return buffer.ToArray();
    }

    public static RuntimeRoom Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaxBytes)
            throw new InvalidDataException("The room package is empty or too large.");
        try
        {
            var package = JsonSerializer.Deserialize<Envelope>(bytes, Json)
                ?? throw new InvalidDataException("The room package has no manifest.");
            if (package.Version != 1 || package.RuntimeProfile != RuntimeProfile)
                throw new InvalidDataException("This room package needs a different player profile.");
            var payload = Encoding.UTF8.GetBytes(package.Room.GetRawText());
            if (package.Sha256 != Digest(payload))
                throw new InvalidDataException("The room package checksum does not match.");
            var room = JsonSerializer.Deserialize<RuntimeRoom>(payload, Json)
                ?? throw new InvalidDataException("The room package is missing its scene.");
            Validate(room);
            return room with
            {
                Objects = Array.AsReadOnly(room.Objects.ToArray()),
                Switches = Array.AsReadOnly(room.Switches.ToArray())
            };
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The room package contains invalid JSON or fields.", error);
        }
    }

    public static RuntimeRoom Open(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length is 0 or > MaxBytes)
            throw new InvalidDataException("The room package is empty or too large.");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        return Decode(bytes);
    }

    public static void Save(string path, RuntimeRoom room, bool overwrite = false)
    {
        var destination = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(destination), Extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Choose a {Extension} filename.", nameof(path));
        var bytes = Encode(room);
        var staging = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            if (overwrite && File.Exists(destination))
            {
                // Only replace a file that is already a valid room package.
                Open(destination);
                File.Replace(staging, destination, null);
            }
            else
            {
                File.Move(staging, destination);
            }
        }
        finally
        {
            try { File.Delete(staging); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void Validate(RuntimeRoom room)
    {
        if (room.ProjectId == Guid.Empty || room.SceneId == Guid.Empty || room.ProjectId == room.SceneId ||
            room.SourceRevision < 1 || !Label(room.Name) || room.Objects is null || room.Switches is null ||
            room.Objects.Count > MaxRecords || room.Switches.Count > MaxRecords)
            throw new InvalidDataException("The room package has invalid identity, labels, or size.");

        var ids = new HashSet<Guid> { room.ProjectId, room.SceneId };
        var targets = new HashSet<Guid>();
        foreach (var item in room.Objects)
        {
            if (item is null || item.Id == Guid.Empty || !ids.Add(item.Id) || item.AssetId == Guid.Empty ||
                !Label(item.Name) || !Label(item.Location) || !(item.X is >= 0 and <= 1) || !(item.Y is >= 0 and <= 1))
                throw new InvalidDataException("The room package has an invalid lantern.");
            targets.Add(item.Id);
        }
        foreach (var item in room.Switches)
        {
            if (item is null || item.Id == Guid.Empty || !ids.Add(item.Id) || !Label(item.Name) ||
                !targets.Contains(item.TargetId) || (item.Behavior is { } behavior && !Enum.IsDefined(behavior)))
                throw new InvalidDataException("The room package has an invalid switch or target.");
        }
    }

    private static bool Label(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256;
    private static string Digest(byte[] payload) => Convert.ToHexStringLower(SHA256.HashData(payload));
    private sealed record Envelope(int Version, string RuntimeProfile, string Sha256, JsonElement Room);
}
