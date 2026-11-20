using System.Text.Json.Serialization;

namespace PlatzAtlas.Core;

public sealed record SceneDocument(IReadOnlyList<SceneEntity> Entities, IReadOnlyList<SceneEdit> History)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Guid>? ActiveAssetIds { get; init; }
}
public sealed record SceneEntity(Guid Id, string Name, Transform2D Transform, SceneComponent Component);

// Coordinates and size are fractions of the room. Positions identify the center.
public readonly record struct Transform2D(double X, double Y, double Width, double Height, double Rotation)
{
    public bool Contains(double x, double y)
        => IntersectsCircle(x, y, 0);

    public bool IntersectsCircle(double x, double y, double radius)
    {
        var angle = Rotation * Math.PI / 180;
        var dx = x - X;
        var dy = y - Y;
        var outsideX = Math.Max(0, Math.Abs(dx * Math.Cos(angle) + dy * Math.Sin(angle)) - Width / 2);
        var outsideY = Math.Max(0, Math.Abs(dy * Math.Cos(angle) - dx * Math.Sin(angle)) - Height / 2);
        return outsideX * outsideX + outsideY * outsideY <= radius * radius;
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WallPanel), "wall")]
[JsonDerivedType(typeof(SceneLamp), "lamp")]
[JsonDerivedType(typeof(SceneDoor), "door")]
[JsonDerivedType(typeof(SceneSwitch), "switch")]
[JsonDerivedType(typeof(SceneSprite), "sprite")]
public abstract record SceneComponent;

public sealed record WallPanel(PanelColor Color) : SceneComponent;
public sealed record SceneLamp(bool IsOn) : SceneComponent;
public sealed record SceneDoor(bool IsOpen) : SceneComponent;
public sealed record SceneSwitch(Guid? TargetId, SwitchBehavior Behavior) : SceneComponent;
public sealed record SceneSprite(Guid AssetId) : SceneComponent;
public enum PanelColor { Slate, Teal, Sand, Plum }

public abstract record SceneOperation(Guid ProjectId, long BaseRevision, Guid EntityId);
public sealed record AddSceneEntity(Guid ProjectId, long BaseRevision, SceneEntity Entity)
    : SceneOperation(ProjectId, BaseRevision, Entity.Id);
public sealed record DeleteSceneEntity(Guid ProjectId, long BaseRevision, Guid EntityId)
    : SceneOperation(ProjectId, BaseRevision, EntityId);
public sealed record RenameSceneEntity(Guid ProjectId, long BaseRevision, Guid EntityId, string Name)
    : SceneOperation(ProjectId, BaseRevision, EntityId);
public sealed record TransformSceneEntity(Guid ProjectId, long BaseRevision, Guid EntityId, Transform2D Transform)
    : SceneOperation(ProjectId, BaseRevision, EntityId);
public sealed record SetScenePanelColor(Guid ProjectId, long BaseRevision, Guid EntityId, PanelColor Color)
    : SceneOperation(ProjectId, BaseRevision, EntityId);
public sealed record SetSceneLamp(Guid ProjectId, long BaseRevision, Guid EntityId, bool IsOn)
    : SceneOperation(ProjectId, BaseRevision, EntityId);
public sealed record SetSceneDoor(Guid ProjectId, long BaseRevision, Guid EntityId, bool IsOpen)
    : SceneOperation(ProjectId, BaseRevision, EntityId);
public sealed record ConfigureSceneSwitch(Guid ProjectId, long BaseRevision, Guid EntityId,
    Guid? TargetId, SwitchBehavior Behavior) : SceneOperation(ProjectId, BaseRevision, EntityId);
public sealed record ImportSceneAsset(Guid ProjectId, long BaseRevision, ProjectAsset Asset)
    : SceneOperation(ProjectId, BaseRevision, Asset.Id);

public sealed record SceneEdit(long Revision, Guid EntityId, SceneEntity? Before, SceneEntity? After,
    LightEditKind Kind, long? ReversesRevision)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AssetPresence? AssetChange { get; init; }
}
public sealed record AssetPresence(bool IsPresent);

public static class SceneValidation
{
    public const int MaxEntities = 256;
    internal const int MaxEdits = 10000;

    public static void ValidateEntities(IReadOnlyList<SceneEntity> entities, Guid projectId, Guid sceneId)
    {
        if (entities.Count > MaxEntities)
            throw new InvalidDataException($"A scene can contain at most {MaxEntities} objects.");
        var ids = new HashSet<Guid> { projectId, sceneId };
        foreach (var entity in entities)
        {
            ValidateEntity(entity);
            if (!ids.Add(entity.Id))
                throw new InvalidDataException("Every scene object needs a unique ID.");
        }
        ValidateLinks(entities);
    }

    public static void ValidateEntity(SceneEntity? entity)
    {
        if (entity is null || entity.Id == Guid.Empty || string.IsNullOrWhiteSpace(entity.Name) ||
            entity.Name.Length > 96 || entity.Name.Any(char.IsControl))
            throw new InvalidDataException("Enter an object name of up to 96 printable characters.");
        if (entity.Transform is not { X: >= 0 and <= 1, Y: >= 0 and <= 1, Width: >= .025 and <= 1,
            Height: >= .025 and <= 1, Rotation: >= -360 and <= 360 })
            throw new InvalidDataException("Position must be 0–1, size 0.025–1 and rotation −360° to 360°.");
        if (entity.Component is not (WallPanel or SceneLamp or SceneDoor or SceneSwitch or SceneSprite) ||
            (entity.Component is WallPanel panel && !Enum.IsDefined(panel.Color)) ||
            (entity.Component is SceneSwitch control && !Enum.IsDefined(control.Behavior)) ||
            (entity.Component is SceneSprite sprite && sprite.AssetId == Guid.Empty))
            throw new InvalidDataException("The scene object has an unsupported component or value.");
    }

    internal static void ValidateLinks(IEnumerable<SceneEntity> entities)
    {
        var targets = entities.Where(entity => entity.Component is SceneDoor or SceneLamp)
            .Select(entity => entity.Id).ToHashSet();
        if (entities.Any(entity => entity.Component is SceneSwitch { TargetId: Guid target } && !targets.Contains(target)))
            throw new InvalidDataException("A switch target is missing or is not a door or lamp. Unlink it before deleting the target.");
    }

    internal static void ValidateImages(IEnumerable<SceneEntity> entities, IReadOnlySet<Guid> activeAssets)
    {
        if (entities.Any(item => activeAssets.Contains(item.Id) ||
            (item.Component is SceneSprite sprite && !activeAssets.Contains(sprite.AssetId))))
            throw new InvalidDataException("A scene image references an asset that is missing or inactive.");
    }
}

internal sealed class SceneJournal
{
    private readonly Stack<SceneEdit> undo = new();
    private readonly Stack<SceneEdit> redo = new();

    public SceneEdit? NextUndo => undo.TryPeek(out var edit) ? edit : null;
    public SceneEdit? NextRedo => redo.TryPeek(out var edit) ? edit : null;

    public void Record(SceneEdit edit)
    {
        if (edit.Kind == LightEditKind.Edit && edit.ReversesRevision is null)
        {
            undo.Push(edit);
            redo.Clear();
            return;
        }

        var (source, destination) = edit.Kind switch
        {
            LightEditKind.Undo => (undo, redo),
            LightEditKind.Redo => (redo, undo),
            _ => throw new InvalidDataException("The scene history contains an invalid edit.")
        };
        if (!source.TryPeek(out var prior) || edit.ReversesRevision != prior.Revision ||
            edit.EntityId != prior.EntityId || edit.Before != prior.After || edit.After != prior.Before ||
            edit.AssetChange != InvertAssetChange(prior.AssetChange))
            throw new InvalidDataException("The scene history contains an invalid undo or redo.");
        source.Pop();
        destination.Push(edit);
    }

    public static AssetPresence? InvertAssetChange(AssetPresence? change) =>
        change is null ? null : new AssetPresence(!change.IsPresent);

    public static SceneDocument Validate(SceneDocument scene, IReadOnlyList<ProjectAsset> assets,
        Guid projectId, Guid sceneId, long revision)
    {
        SceneValidation.ValidateEntities(scene.Entities, projectId, sceneId);
        if (scene.History.Count > SceneValidation.MaxEdits || revision != scene.History.Count + MuseumProject.InitialRevision)
            throw new InvalidDataException("The saved revision does not match its scene history.");

        var state = new Dictionary<Guid, SceneEntity>();
        var created = new HashSet<Guid>();
        var catalog = assets.Select(item => item.Id).ToHashSet();
        var imported = new HashSet<Guid>();
        var activeAssets = new HashSet<Guid>();
        var journal = new SceneJournal();
        var expected = MuseumProject.InitialRevision;
        foreach (var edit in scene.History)
        {
            if (edit is null || edit.Revision != ++expected || edit.EntityId == projectId || edit.EntityId == sceneId)
                throw new InvalidDataException("The saved scene edit has an invalid revision or ID.");
            journal.Record(edit);
            if (edit.AssetChange is { } presence)
            {
                if (edit.Before is not null || edit.After is not null || !catalog.Contains(edit.EntityId) ||
                    (edit.Kind == LightEditKind.Edit && (!presence.IsPresent || !imported.Add(edit.EntityId))) ||
                    !(presence.IsPresent ? activeAssets.Add(edit.EntityId) : activeAssets.Remove(edit.EntityId)))
                    throw new InvalidDataException("The saved scene has invalid asset history.");
                SceneValidation.ValidateImages(state.Values, activeAssets);
                continue;
            }
            if (catalog.Contains(edit.EntityId) ||
                edit.Before == edit.After || edit.Before != state.GetValueOrDefault(edit.EntityId) ||
                (edit.Kind == LightEditKind.Edit && edit.Before is null && !created.Add(edit.EntityId)) ||
                (edit.After is { } after && (after.Id != edit.EntityId ||
                    (edit.Before is { } before && before.Component.GetType() != after.Component.GetType()))))
                throw new InvalidDataException("The saved scene edit does not match its object.");
            if (edit.After is null)
                state.Remove(edit.EntityId);
            else
            {
                SceneValidation.ValidateEntity(edit.After);
                state[edit.EntityId] = edit.After;
            }
            if (state.Count > SceneValidation.MaxEntities)
                throw new InvalidDataException("The scene history exceeds the object limit.");
            SceneValidation.ValidateLinks(state.Values);
            SceneValidation.ValidateImages(state.Values, activeAssets);
        }

        if (state.Count != scene.Entities.Count ||
            scene.Entities.Any(entity => state.GetValueOrDefault(entity.Id) != entity))
            throw new InvalidDataException("The saved scene objects do not match the scene history.");
        var listedAssets = scene.ActiveAssetIds ?? [];
        if (!catalog.SetEquals(imported) || listedAssets.Count != activeAssets.Count || !activeAssets.SetEquals(listedAssets))
            throw new InvalidDataException("The image catalog does not match its history.");
        return new SceneDocument(Array.AsReadOnly(scene.Entities.OrderBy(item => item.Id).ToArray()),
            Array.AsReadOnly(scene.History.ToArray()))
        {
            ActiveAssetIds = activeAssets.Count == 0 ? null : Array.AsReadOnly(activeAssets.Order().ToArray())
        };
    }
}
