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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<SceneEntity>? Entities { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ProjectAsset>? RasterAssets { get; init; }

    public static RuntimeRoom FromProject(MuseumProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return new RuntimeRoom(project.ProjectId, project.SceneId, project.Revision, project.Name,
            Array.AsReadOnly(project.Objects.ToArray()), Array.AsReadOnly(project.Switches.ToArray()))
        {
            Entities = project.Scene?.Entities,
            RasterAssets = project.Scene is { } scene ? SceneAssets(scene, project.Assets) : null
        };
    }

    private static IReadOnlyList<ProjectAsset>? SceneAssets(SceneDocument scene, IReadOnlyList<ProjectAsset> assets)
    {
        var needed = scene.Entities.Select(item => item.Component).OfType<SceneSprite>().Select(item => item.AssetId).ToHashSet();
        return needed.Count == 0 ? null : Array.AsReadOnly(assets.Where(item => needed.Contains(item.Id)).ToArray());
    }
}

public static class RoomPackage
{
    public const string Extension = ".platzroom";
    public const string RuntimeProfile = "night-museum/1";
    public const string SceneProfile = "room-scene/2";
    private const int MuseumMaxBytes = 256 * 1024;
    private const int MaxBytes = 2 * 1024 * 1024;
    private const int MaxRecords = 128;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12,
        Converters =
        {
            new JsonStringEnumConverter<SwitchBehavior>(JsonNamingPolicy.CamelCase, false),
            new JsonStringEnumConverter<PanelColor>(JsonNamingPolicy.CamelCase, false)
        }
    };

    public static byte[] Encode(RuntimeRoom room)
    {
        Validate(room);
        var payload = JsonSerializer.SerializeToUtf8Bytes(room, Json);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", room.Entities is null ? 1 : 2);
            writer.WriteString("runtimeProfile", room.Entities is null ? RuntimeProfile : SceneProfile);
            writer.WriteString("sha256", Digest(payload));
            writer.WritePropertyName("room");
            writer.WriteRawValue(payload);
            writer.WriteEndObject();
        }
        if (buffer.Length > (room.Entities is null ? MuseumMaxBytes : MaxBytes))
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
            if ((package.Version, package.RuntimeProfile) is not ((1, RuntimeProfile) or (2, SceneProfile)))
                throw new InvalidDataException("This room package needs a different player profile.");
            if (package.Version == 1 && bytes.Length > MuseumMaxBytes)
                throw new InvalidDataException("The museum package exceeds the supported size.");
            var payload = Encoding.UTF8.GetBytes(package.Room.GetRawText());
            if (!string.Equals(package.Sha256, Digest(payload), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The room package checksum does not match.");
            var room = JsonSerializer.Deserialize<RuntimeRoom>(payload, Json)
                ?? throw new InvalidDataException("The room package is missing its scene.");
            if ((package.Version == 1) != (room.Entities is null))
                throw new InvalidDataException("The scene does not match the declared player profile.");
            Validate(room);
            return room with
            {
                Objects = Array.AsReadOnly(room.Objects.ToArray()),
                Switches = Array.AsReadOnly(room.Switches.ToArray()),
                Entities = room.Entities is null ? null : Array.AsReadOnly(room.Entities.ToArray()),
                RasterAssets = room.RasterAssets is null ? null : Array.AsReadOnly(room.RasterAssets.ToArray())
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

        if (room.Entities is { } entities)
        {
            if (room.Objects.Count != 0 || room.Switches.Count != 0)
                throw new InvalidDataException("The scene contains incompatible museum records.");
            SceneValidation.ValidateEntities(entities, room.ProjectId, room.SceneId);
            PixelAsset.ValidateRuntime(room.RasterAssets ?? [], entities, room.ProjectId, room.SceneId);
            MuseumPlaySession.FindSpawn(entities);
            return;
        }
        if (room.RasterAssets is not null)
            throw new InvalidDataException("The museum package has unexpected image assets.");

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
