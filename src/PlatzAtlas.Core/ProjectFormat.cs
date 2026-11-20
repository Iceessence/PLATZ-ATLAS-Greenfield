using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlatzAtlas.Core;

internal static class ProjectFormat
{
    private static readonly JsonSerializerOptions Settings = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<LightEditKind>(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    public static MuseumProject Read(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                root.EnumerateObject().Count() != 2 ||
                !root.TryGetProperty("sha256", out var checksum) ||
                checksum.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("project", out var project))
            {
                throw new InvalidDataException("This file is missing its PLATZ project envelope.");
            }

            var payload = Encoding.UTF8.GetBytes(project.GetRawText());
            if (!string.Equals(checksum.GetString(), Digest(payload), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The project checksum does not match the file contents.");
            }

            if (project.ValueKind != JsonValueKind.Object ||
                !project.TryGetProperty("schemaVersion", out var schema) ||
                schema.ValueKind != JsonValueKind.Number ||
                !schema.TryGetInt32(out var version) ||
                version != MuseumProject.FormatVersion)
            {
                throw new InvalidDataException("This editor supports PLATZ project schema 1.");
            }

            var snapshot = JsonSerializer.Deserialize<MuseumProject>(payload, Settings)
                ?? throw new InvalidDataException("The project record is empty.");
            return Validate(snapshot);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The project is incomplete or contains invalid JSON data.", exception);
        }
    }

    public static byte[] Write(MuseumProject project)
    {
        var payload = JsonSerializer.Serialize(project, Settings)
            .Replace("\n", "\n  ", StringComparison.Ordinal);
        var checksum = Digest(Encoding.UTF8.GetBytes(payload));
        return Encoding.UTF8.GetBytes($"{{\n  \"sha256\": \"{checksum}\",\n  \"project\": {payload}\n}}\n");
    }

    public static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static MuseumProject Validate(MuseumProject project)
    {
        if (project.ProjectId == Guid.Empty ||
            project.SceneId == Guid.Empty ||
            project.ProjectId == project.SceneId ||
            string.IsNullOrWhiteSpace(project.Name) ||
            project.Units != MuseumProject.CoordinateUnits ||
            project.Origin != MuseumProject.CoordinateOrigin)
        {
            throw new InvalidDataException("The project has an invalid identity or room coordinate convention.");
        }

        var identities = new HashSet<Guid> { project.ProjectId, project.SceneId };
        var assets = new HashSet<Guid>();
        foreach (var asset in project.Assets)
        {
            if (asset is null || asset.Id == Guid.Empty || !identities.Add(asset.Id) ||
                string.IsNullOrWhiteSpace(asset.Name) || string.IsNullOrWhiteSpace(asset.MediaType) ||
                string.IsNullOrEmpty(asset.ContentBase64))
            {
                throw new InvalidDataException("The project contains an invalid asset.");
            }

            ValidateAssetDigest(asset);
            assets.Add(asset.Id);
        }

        var lights = new Dictionary<Guid, bool>();
        foreach (var lantern in project.Objects)
        {
            if (lantern is null || lantern.Id == Guid.Empty || !identities.Add(lantern.Id) ||
                string.IsNullOrWhiteSpace(lantern.Name) || string.IsNullOrWhiteSpace(lantern.Location) ||
                !CoordinateInRoom(lantern.X) || !CoordinateInRoom(lantern.Y) || !assets.Contains(lantern.AssetId))
            {
                throw new InvalidDataException("The room has an invalid lantern, position, or asset reference.");
            }

            lights.Add(lantern.Id, lantern.IsOn);
        }

        var switchTargets = new Dictionary<Guid, Guid>();
        foreach (var lightSwitch in project.Switches)
        {
            if (lightSwitch is null || lightSwitch.Id == Guid.Empty || !identities.Add(lightSwitch.Id) ||
                string.IsNullOrWhiteSpace(lightSwitch.Name) || !lights.ContainsKey(lightSwitch.TargetId))
            {
                throw new InvalidDataException("The room has an invalid switch or a missing switch target.");
            }

            switchTargets.Add(lightSwitch.Id, lightSwitch.TargetId);
        }

        ValidateHistory(project, lights, switchTargets);
        return project with
        {
            Objects = Array.AsReadOnly(project.Objects.ToArray()),
            Switches = Array.AsReadOnly(project.Switches.ToArray()),
            Assets = Array.AsReadOnly(project.Assets.ToArray()),
            History = Array.AsReadOnly(project.History.ToArray())
        };
    }

    private static bool CoordinateInRoom(double coordinate) => coordinate is >= 0 and <= 1;

    private static void ValidateAssetDigest(ProjectAsset asset)
    {
        try
        {
            if (!string.Equals(Digest(Convert.FromBase64String(asset.ContentBase64)), asset.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The checksum for the asset \"{asset.Name}\" does not match.");
            }
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException($"The asset \"{asset.Name}\" is incomplete.", exception);
        }
    }

    private static void ValidateHistory(MuseumProject project, Dictionary<Guid, bool> lights,
        Dictionary<Guid, Guid> switchTargets)
    {
        if (project.Revision != MuseumProject.InitialRevision + project.History.Count)
        {
            throw new InvalidDataException("The saved revision does not match its light history.");
        }

        var expectedRevision = project.Revision;
        foreach (var edit in project.History.Reverse())
        {
            if (edit is null || edit.Revision != expectedRevision ||
                !lights.TryGetValue(edit.ObjectId, out var value) ||
                edit.IsOn != value || edit.IsOn == edit.WasOn ||
                (edit.SwitchId is Guid switchId &&
                    (!switchTargets.TryGetValue(switchId, out var target) || target != edit.ObjectId)))
            {
                throw new InvalidDataException("The saved light history does not match the room.");
            }

            lights[edit.ObjectId] = edit.WasOn;
            expectedRevision--;
        }

        var history = new LightHistory();
        foreach (var edit in project.History)
        {
            history.Record(edit);
        }
    }
}
