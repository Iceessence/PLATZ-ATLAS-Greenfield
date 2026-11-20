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
        Converters =
        {
            new JsonStringEnumConverter<LightEditKind>(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
            new JsonStringEnumConverter<SwitchBehavior>(JsonNamingPolicy.CamelCase, allowIntegerValues: false),
            new JsonStringEnumConverter<PanelColor>(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
        }
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
                version is < MuseumProject.LegacyFormatVersion or > MuseumProject.SceneFormatVersion)
            {
                throw new InvalidDataException("This editor supports PLATZ project schemas 1 through 4.");
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

    public static string SceneEditDigest(Guid projectId, Guid sceneId, SceneEdit edit) =>
        Digest(JsonSerializer.SerializeToUtf8Bytes(new
        {
            projectId, sceneId, baseRevision = edit.Revision - 1, edit.EntityId, edit.Before, edit.After
        }, Settings));

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

        if (project.SchemaVersion == MuseumProject.SceneFormatVersion)
        {
            if (project.Scene is null || project.Objects.Count != 0 || project.Switches.Count != 0 || project.History.Count != 0)
                throw new InvalidDataException("The scene document contains incompatible project records.");
            PixelAsset.ValidateCatalog(project.Assets, project.ProjectId, project.SceneId);
            return project with
            {
                Scene = SceneJournal.Validate(project.Scene, project.Assets, project.ProjectId, project.SceneId, project.Revision),
                Objects = [], Switches = [], Assets = Array.AsReadOnly(project.Assets.OrderBy(asset => asset.Id).ToArray()), History = []
            };
        }

        if (project.Scene is not null)
            throw new InvalidDataException("Scene objects require project schema 4.");

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

            if (lightSwitch.Behavior is not null && project.SchemaVersion < MuseumProject.FormatVersion)
                throw new InvalidDataException("Switch behavior requires project schema 3.");
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
        var operationIds = new HashSet<Guid>();
        var behaviors = project.Switches.ToDictionary(item => item.Id, item => item.Behavior ?? SwitchBehavior.Toggle);
        foreach (var edit in project.History.Reverse())
        {
            if (edit is null || edit.Revision != expectedRevision ||
                !lights.TryGetValue(edit.ObjectId, out var value) ||
                edit.IsOn != value ||
                (edit.SwitchId is Guid switchId &&
                    (!switchTargets.TryGetValue(switchId, out var target) || target != edit.ObjectId)))
            {
                throw new InvalidDataException("The saved light history does not match the room.");
            }

            if (edit.BehaviorChange is { } change)
            {
                if (project.SchemaVersion != MuseumProject.FormatVersion || edit.AtlasCommit is not null ||
                    edit.SwitchId is not Guid behaviorSwitchId || edit.WasOn != edit.IsOn ||
                    change.Before == change.After || behaviors[behaviorSwitchId] != change.After)
                    throw new InvalidDataException("The saved switch behavior does not match the edit history.");
                behaviors[behaviorSwitchId] = change.Before;
            }
            else if (edit.IsOn == edit.WasOn)
            {
                throw new InvalidDataException("A light edit must change the light state.");
            }

            if (edit.AtlasCommit is { } stamp &&
                (project.SchemaVersion < MuseumProject.ReceiptFormatVersion || edit.Kind != LightEditKind.Edit ||
                 stamp.OperationId == Guid.Empty || !operationIds.Add(stamp.OperationId) ||
                 edit.SwitchId is not Guid receiptSwitchId ||
                 stamp.PayloadSha256 != LightCommitReceipt.Digest(project.ProjectId, project.SceneId,
                     edit.Revision - 1, edit.ObjectId, receiptSwitchId, edit.WasOn, edit.IsOn)))
            {
                throw new InvalidDataException("The saved ATLAS commit receipt does not match the light edit.");
            }

            lights[edit.ObjectId] = edit.WasOn;
            expectedRevision--;
        }
        if (behaviors.Values.Any(value => value != SwitchBehavior.Toggle))
            throw new InvalidDataException("The room must start with toggle switches before its edit history.");

        var history = new LightHistory();
        foreach (var edit in project.History)
        {
            history.Record(edit);
        }
    }
}
