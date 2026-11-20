using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlatzAtlas.Atlas;

public readonly record struct ProjectReadScope(Guid ProjectId, Guid SceneId, long Revision)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? WorkspaceId { get; init; }
    internal bool IsValid => ProjectId != Guid.Empty && SceneId != Guid.Empty && Revision > 0 && WorkspaceId != Guid.Empty;
}

public sealed record SwitchSource(Guid Id, string Name, Guid TargetId)
{
    public string TargetField => $"switches[{Id:D}].targetId";
}

public sealed record LanternSource(Guid Id, Guid AssetId, string Name, string Location, bool IsOn)
{
    public string LocationField => $"objects[{Id:D}].location";
    public string LightField => $"objects[{Id:D}].isOn";
}

// Versioned UTF-8 document shared by the project host and the read-only resolver.
public static class SwitchEvidenceProfile
{
    public const int Version = 1;
    public const int MaxRecordsPerKind = 128;
    public const int MaxLabelLength = 256;
    private const int MaxDocumentBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        IgnoreReadOnlyProperties = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12
    };

    public static byte[] Publish(ProjectReadScope scope, SwitchSource[] switches, LanternSource[] objects)
    {
        var snapshot = new Snapshot(Version, scope, switches, objects);
        if (!IsValid(snapshot))
            throw new InvalidDataException("The project exceeds the supported switch evidence profile.");

        var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot, Options);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("sha256", Digest(payload));
            writer.WritePropertyName("snapshot");
            writer.WriteRawValue(payload);
            writer.WriteEndObject();
        }

        if (buffer.WrittenCount > MaxDocumentBytes)
            throw new InvalidDataException("The switch evidence snapshot is too large.");
        return buffer.WrittenSpan.ToArray();
    }

    internal static (QuestionStatus Status, Snapshot? Data, string? Sha256) Read(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is 0 or > MaxDocumentBytes)
            return (QuestionStatus.InvalidSnapshot, null, null);

        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(bytes.Span, Options);
            if (envelope?.Snapshot.ValueKind != JsonValueKind.Object)
                return (QuestionStatus.InvalidSnapshot, null, null);

            var payload = Encoding.UTF8.GetBytes(envelope.Snapshot.GetRawText());
            if (!string.Equals(envelope.Sha256, Digest(payload), StringComparison.OrdinalIgnoreCase))
                return (QuestionStatus.InvalidSnapshot, null, null);

            if (!envelope.Snapshot.TryGetProperty("profileVersion", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var profileVersion))
                return (QuestionStatus.InvalidSnapshot, null, null);
            if (profileVersion != Version)
                return (QuestionStatus.IncompatibleProfile, null, null);

            var snapshot = JsonSerializer.Deserialize<Snapshot>(payload, Options);
            return snapshot is not null && IsValid(snapshot)
                ? (QuestionStatus.Answered, snapshot, envelope.Sha256)
                : (QuestionStatus.InvalidSnapshot, null, null);
        }
        catch (JsonException)
        {
            return (QuestionStatus.InvalidSnapshot, null, null);
        }
    }

    private static bool IsValid(Snapshot snapshot)
    {
        if (!snapshot.Scope.IsValid || snapshot.Switches is null || snapshot.Objects is null
            || snapshot.Switches.Length > MaxRecordsPerKind
            || snapshot.Objects.Length > MaxRecordsPerKind)
            return false;

        var ids = new HashSet<Guid>();
        foreach (var lantern in snapshot.Objects)
        {
            if (lantern is null || lantern.Id == Guid.Empty || lantern.AssetId == Guid.Empty
                || !LabelIsValid(lantern.Name) || !LabelIsValid(lantern.Location) || !ids.Add(lantern.Id))
                return false;
        }
        foreach (var item in snapshot.Switches)
        {
            if (item is null || item.Id == Guid.Empty || item.TargetId == Guid.Empty
                || !LabelIsValid(item.Name) || !ids.Add(item.Id))
                return false;
        }
        return true;
    }

    private static bool LabelIsValid(string label) =>
        !string.IsNullOrWhiteSpace(label) && label.Length <= MaxLabelLength;

    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed record Envelope(string Sha256, JsonElement Snapshot);
    internal sealed record Snapshot(
        int ProfileVersion, ProjectReadScope Scope, SwitchSource[] Switches, LanternSource[] Objects);
}
