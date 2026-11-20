using System.Text.Json.Serialization;

namespace PlatzAtlas.Core;

public sealed record SceneDocument(IReadOnlyList<SceneEntity> Entities, IReadOnlyList<SceneEdit> History);
public sealed record SceneEntity(Guid Id, string Name, Transform2D Transform, SceneComponent Component);

// Coordinates and size are fractions of the room. Positions identify the center.
public readonly record struct Transform2D(double X, double Y, double Width, double Height, double Rotation)
{
    public bool Contains(double x, double y)
    {
        var angle = Rotation * Math.PI / 180;
        var dx = x - X;
        var dy = y - Y;
        return Math.Abs(dx * Math.Cos(angle) + dy * Math.Sin(angle)) <= Width / 2 &&
            Math.Abs(dy * Math.Cos(angle) - dx * Math.Sin(angle)) <= Height / 2;
    }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WallPanel), "wall")]
[JsonDerivedType(typeof(SceneLamp), "lamp")]
public abstract record SceneComponent;

public sealed record WallPanel(PanelColor Color) : SceneComponent;
public sealed record SceneLamp(bool IsOn) : SceneComponent;
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

public sealed record SceneEdit(long Revision, Guid EntityId, SceneEntity? Before, SceneEntity? After,
    LightEditKind Kind, long? ReversesRevision);

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
    }

    public static void ValidateEntity(SceneEntity? entity)
    {
        if (entity is null || entity.Id == Guid.Empty || string.IsNullOrWhiteSpace(entity.Name) ||
            entity.Name.Length > 96 || entity.Name.Any(char.IsControl))
            throw new InvalidDataException("Enter an object name of up to 96 printable characters.");
        if (entity.Transform is not { X: >= 0 and <= 1, Y: >= 0 and <= 1, Width: >= .025 and <= 1,
            Height: >= .025 and <= 1, Rotation: >= -360 and <= 360 })
            throw new InvalidDataException("Position must be 0–1, size 0.025–1 and rotation −360° to 360°.");
        if (entity.Component is not (WallPanel or SceneLamp) ||
            (entity.Component is WallPanel panel && !Enum.IsDefined(panel.Color)))
            throw new InvalidDataException("The scene object has an unsupported component or value.");
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
            edit.EntityId != prior.EntityId || edit.Before != prior.After || edit.After != prior.Before)
            throw new InvalidDataException("The scene history contains an invalid undo or redo.");
        source.Pop();
        destination.Push(edit);
    }

    public static SceneDocument Validate(SceneDocument scene, Guid projectId, Guid sceneId, long revision)
    {
        SceneValidation.ValidateEntities(scene.Entities, projectId, sceneId);
        if (scene.History.Count > SceneValidation.MaxEdits || revision != scene.History.Count + MuseumProject.InitialRevision)
            throw new InvalidDataException("The saved revision does not match its scene history.");

        var state = new Dictionary<Guid, SceneEntity>();
        var created = new HashSet<Guid>();
        var journal = new SceneJournal();
        var expected = MuseumProject.InitialRevision;
        foreach (var edit in scene.History)
        {
            if (edit is null || edit.Revision != ++expected || edit.EntityId == projectId || edit.EntityId == sceneId ||
                edit.Before == edit.After || edit.Before != state.GetValueOrDefault(edit.EntityId) ||
                (edit.Kind == LightEditKind.Edit && edit.Before is null && !created.Add(edit.EntityId)) ||
                (edit.After is { } after && (after.Id != edit.EntityId ||
                    (edit.Before is { } before && before.Component.GetType() != after.Component.GetType()))))
                throw new InvalidDataException("The saved scene edit does not match its object.");
            journal.Record(edit);
            if (edit.After is null)
                state.Remove(edit.EntityId);
            else
            {
                SceneValidation.ValidateEntity(edit.After);
                state[edit.EntityId] = edit.After;
            }
            if (state.Count > SceneValidation.MaxEntities)
                throw new InvalidDataException("The scene history exceeds the object limit.");
        }

        if (state.Count != scene.Entities.Count ||
            scene.Entities.Any(entity => state.GetValueOrDefault(entity.Id) != entity))
            throw new InvalidDataException("The saved scene objects do not match the scene history.");
        return new SceneDocument(Array.AsReadOnly(scene.Entities.OrderBy(item => item.Id).ToArray()),
            Array.AsReadOnly(scene.History.ToArray()));
    }
}
