using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlatzAtlas.Atlas;

public enum SceneSourceKind { Wall, Lamp, Door, Switch, Image }
public sealed record SceneSource(Guid Id, string Name, SceneSourceKind Kind, double X, double Y, double Width,
    double Height, double Rotation, bool? Active, Guid? TargetId, string? Behavior, Guid? AssetId, string? AssetName, string? Color);
public sealed record SceneEvidence(ProjectReadScope Scope, IReadOnlyList<SceneSource> Objects);

public static class SceneEvidenceProfile
{
    public const int Version = 2;
    private const int MaxBytes = 256 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 10,
        Converters = { new JsonStringEnumConverter<SceneSourceKind>(JsonNamingPolicy.CamelCase, false) }
    };

    public static byte[] Publish(ProjectReadScope scope, IReadOnlyList<SceneSource> objects)
    {
        var evidence = new SceneEvidence(scope, objects.OrderBy(item => item.Id).ToArray());
        Validate(evidence);
        var payload = JsonSerializer.SerializeToUtf8Bytes(evidence, Json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", Version);
            writer.WriteString("sha256", Digest(payload));
            writer.WritePropertyName("scene");
            writer.WriteRawValue(payload);
            writer.WriteEndObject();
        }
        if (stream.Length > MaxBytes)
            throw new InvalidDataException("Scene evidence exceeds its supported size.");
        return stream.ToArray();
    }

    public static (SceneEvidence Evidence, string Sha256) Open(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaxBytes)
            throw new InvalidDataException("Scene evidence is empty or too large.");
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(bytes, Json)
                ?? throw new InvalidDataException("Scene evidence is missing its envelope.");
            if (envelope.Version != Version)
                throw new InvalidDataException("This scene evidence needs a different profile.");
            var payload = Encoding.UTF8.GetBytes(envelope.Scene.GetRawText());
            var sha = Digest(payload);
            if (!string.Equals(envelope.Sha256, sha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Scene evidence failed checksum validation.");
            var evidence = JsonSerializer.Deserialize<SceneEvidence>(payload, Json)
                ?? throw new InvalidDataException("Scene evidence is incomplete.");
            Validate(evidence);
            return (evidence with { Objects = Array.AsReadOnly(evidence.Objects.ToArray()) }, sha);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Scene evidence has invalid records.", error);
        }
    }

    private static void Validate(SceneEvidence evidence)
    {
        if (!evidence.Scope.IsValid || evidence.Objects.Count > 256)
            throw new InvalidDataException("Scene evidence has invalid identity or too many records.");
        var ids = new HashSet<Guid> { evidence.Scope.ProjectId, evidence.Scope.SceneId };
        var targets = new HashSet<Guid>();
        foreach (var item in evidence.Objects)
        {
            if (item is null || item.Id == Guid.Empty || !ids.Add(item.Id) || string.IsNullOrWhiteSpace(item.Name) ||
                item.Name.Length > 96 || item.Name.Any(char.IsControl) || !Enum.IsDefined(item.Kind) ||
                item is not { X: >= 0 and <= 1, Y: >= 0 and <= 1, Width: >= .025 and <= 1,
                    Height: >= .025 and <= 1, Rotation: >= -360 and <= 360 })
                throw new InvalidDataException("Scene evidence contains an invalid object.");
            if (item.Kind is SceneSourceKind.Door or SceneSourceKind.Lamp)
                targets.Add(item.Id);
            var propertiesMatch = item.Kind switch
            {
                SceneSourceKind.Door or SceneSourceKind.Lamp => item.Active is not null && item.Color is null &&
                    item.TargetId is null && item.Behavior is null && item.AssetId is null && item.AssetName is null,
                SceneSourceKind.Switch => item.Active is null && item.Color is null && item.AssetId is null &&
                    item.AssetName is null && item.Behavior is "Toggle" or "TurnOn",
                SceneSourceKind.Image => item.Active is null && item.Color is null && item.TargetId is null &&
                    item.Behavior is null && item.AssetId is Guid asset && asset != Guid.Empty &&
                    !string.IsNullOrWhiteSpace(item.AssetName) && item.AssetName.Length <= 160,
                SceneSourceKind.Wall => item.Active is null && item.Color is "Slate" or "Teal" or "Sand" or "Plum" &&
                    item.TargetId is null && item.Behavior is null && item.AssetId is null && item.AssetName is null,
                _ => false
            };
            if (!propertiesMatch)
                throw new InvalidDataException("Scene evidence contains properties outside its declared object type.");
        }
        if (evidence.Objects.Any(item => item.TargetId is Guid target && !targets.Contains(target)))
            throw new InvalidDataException("Scene evidence contains a missing link target.");
    }

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private sealed record Envelope(int Version, string Sha256, JsonElement Scene);
}
