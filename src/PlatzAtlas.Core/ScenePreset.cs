using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlatzAtlas.Core;

public sealed record ScenePreset(Guid Id, Guid? ParentId, string Title, string Category, string Description,
    SceneEntity Template)
{
    public const string Extension = ".platzpreset";
    private const int MaximumBytes = 24 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, false) }
    };

    public static ScenePreset FromObject(SceneEntity entity) => new(Guid.NewGuid(), entity.PresetId, entity.Name,
        "Custom", $"Template derived from {entity.Name}.", entity with
        {
            PresetId = null,
            Component = entity.Component is SceneSwitch control
                ? new SceneSwitch(null, control.Behavior) : entity.Component
        });

    public SceneEntity Place(Guid id, Transform2D transform, string name, SceneComponent component)
    {
        Validate();
        if (component.GetType() != Template.Component.GetType())
            throw new InvalidDataException("A variant must use the same component type as its preset.");
        var item = new SceneEntity(id, name.Trim(), transform, component) { PresetId = Id };
        SceneValidation.ValidateEntity(item);
        return item;
    }

    public byte[] Encode()
    {
        Validate();
        return JsonSerializer.SerializeToUtf8Bytes(new PresetEnvelope(1, this), Json);
    }

    public static ScenePreset Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumBytes)
            throw new InvalidDataException("Choose a PLATZ preset up to 24 KiB.");
        try
        {
            var file = JsonSerializer.Deserialize<PresetEnvelope>(bytes, Json)
                ?? throw new InvalidDataException("The preset is empty.");
            if (file.Version != 1)
                throw new InvalidDataException("The preset format version is unsupported.");
            file.Preset.Validate();
            return file.Preset;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("The preset contains invalid fields.", error);
        }
    }

    private void Validate()
    {
        if (Id == Guid.Empty || ParentId == Guid.Empty || ParentId == Id || !Fits(Title, 96) ||
            !Fits(Category, 40) || !Fits(Description, 220))
            throw new InvalidDataException("The preset has invalid metadata.");
        SceneValidation.ValidateEntity(Template);
        if (Template.PresetId is not null || Template.Component is SceneSprite or SceneSwitch { TargetId: not null })
            throw new InvalidDataException("A reusable preset cannot retain room-specific asset or switch references.");

        static bool Fits(string value, int max) =>
            !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);
    }

    private sealed record PresetEnvelope(int Version, ScenePreset Preset);
}

public static class StarterPresets
{
    public static IReadOnlyList<ScenePreset> Items { get; } = Array.AsReadOnly(new[]
    {
        Make(1, SceneObjectKind.Wall, "Bulkhead", "Structure", "A solid wall for boundaries and corridors."),
        Make(2, SceneObjectKind.Door, "Shutter", "Structure", "A solid door that a switch can open."),
        Make(3, SceneObjectKind.Lamp, "Signal lamp", "Lighting", "A light with an independently editable on or off state."),
        Make(4, SceneObjectKind.Switch, "Relay switch", "Logic", "A nearby player can activate up to two linked targets."),
        Make(5, SceneObjectKind.Spawn, "Player start", "Gameplay", "Sets the starting location when this room plays."),
        Make(6, SceneObjectKind.Key, "Collectible key", "Gameplay", "Adds one key to the player's inventory on contact."),
        Make(7, SceneObjectKind.Goal, "Exit pad", "Gameplay", "Completes the room when the player meets its key requirement.")
    });

    private static ScenePreset Make(int index, SceneObjectKind kind, string title, string category, string description)
    {
        var id = new Guid(index, 0x35B, 0x413, [0xA2, 0x14, 0x50, 0x4C, 0x41, 0x54, 0x5A, 1]);
        var (x, y) = kind switch
        {
            SceneObjectKind.Spawn => (.5, .90),
            SceneObjectKind.Goal => (.5, .16),
            _ => (.45, .46)
        };
        return new ScenePreset(id, null, title, category, description,
            SceneObjectFactory.Create(id, kind, kind.ToString(), x, y));
    }
}
