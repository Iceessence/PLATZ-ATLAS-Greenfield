namespace PlatzAtlas.Core;

public sealed partial class ProjectSession
{
    private readonly SceneJournal sceneJournal = new();

    public static ProjectSession NewScene() => new(new MuseumProject(MuseumProject.SceneFormatVersion,
        Guid.NewGuid(), Guid.NewGuid(), "Untitled room", MuseumProject.InitialRevision,
        MuseumProject.CoordinateUnits, MuseumProject.CoordinateOrigin, [], [], [], [])
    {
        Scene = new SceneDocument([], [])
    });

    public bool Apply(SceneOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        CheckEditBase(operation.ProjectId, operation.BaseRevision);
        var scene = Current.Scene ?? throw new ProjectEditException(EditRejection.WrongTarget,
            "Create a blank project to edit scene objects.");
        if (operation is ImportSceneAsset import)
            return ApplyAssetImport(scene, import);
        if (operation is ConfigureRoomAppearance or ShiftSceneLayer)
            return ApplyAppearance(scene, operation);
        if (PlanSceneEdit(scene, operation) is not { } edit)
            return false;
        CommitScene(scene, edit);
        return true;
    }

    private SceneEdit? PlanSceneEdit(SceneDocument scene, SceneOperation operation)
    {
        var prior = scene.Entities.SingleOrDefault(entity => entity.Id == operation.EntityId);
        var creates = operation is AddSceneEntity or DuplicateSceneEntity;
        if (creates && (prior is not null || operation.EntityId == Current.ProjectId ||
            operation.EntityId == Current.SceneId || scene.History.Any(edit => edit.EntityId == operation.EntityId)))
            throw new ProjectEditException(EditRejection.WrongTarget, "This object ID is already in the scene.");
        if (!creates && prior is null)
            throw new ProjectEditException(EditRejection.MissingObject, "This object is no longer in the scene.");
        SceneEntity? next = operation switch
        {
            AddSceneEntity add => add.Entity,
            DuplicateSceneEntity duplicate => Duplicate(scene, duplicate),
            DeleteSceneEntity => null,
            RenameSceneEntity rename => prior! with { Name = rename.Name?.Trim() ?? "" },
            TransformSceneEntity transform => prior! with { Transform = transform.Transform },
            SetScenePanelColor color when prior?.Component is WallPanel =>
                prior with { Component = new WallPanel(color.Color) },
            SetSceneLamp lamp when prior?.Component is SceneLamp =>
                prior with { Component = new SceneLamp(lamp.IsOn) },
            SetSceneDoor door when prior?.Component is SceneDoor =>
                prior with { Component = new SceneDoor(door.IsOpen) },
            ConfigureSceneSwitch control when prior?.Component is SceneSwitch =>
                prior with { Component = new SceneSwitch(control.TargetId, control.Behavior) { SecondTargetId = control.SecondTargetId } },
            SetSceneGoal goal when prior?.Component is SceneGoal =>
                prior with { Component = new SceneGoal(goal.RequiredKeys) },
            _ => throw new ProjectEditException(EditRejection.WrongTarget, "This edit does not apply to the object.")
        };
        return prior == next ? null : new SceneEdit(Current.Revision + 1, operation.EntityId, prior, next, LightEditKind.Edit, null);
    }

    private static SceneEntity Duplicate(SceneDocument scene, DuplicateSceneEntity operation)
    {
        var source = scene.Entities.SingleOrDefault(entity => entity.Id == operation.SourceId)
            ?? throw new ProjectEditException(EditRejection.MissingObject, "The source object is no longer in the room.");
        if (source.Component is SceneSpawn)
            throw new ProjectEditException(EditRejection.WrongTarget, "A room can have only one player spawn.");
        var root = source.Name[..Math.Min(80, source.Name.Length)];
        var names = scene.Entities.Select(entity => entity.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var name = $"{root} copy";
        for (var index = 2; names.Contains(name); index++)
            name = $"{root} copy {index}";
        return source with
        {
            Id = operation.EntityId,
            Name = name,
            Transform = source.Transform with
            {
                X = Math.Min(1, source.Transform.X + .04),
                Y = Math.Min(1, source.Transform.Y + .04)
            }
        };
    }

    private bool ReverseScene(SceneEdit? prior, LightEditKind kind)
    {
        if (prior is null)
            return false;
        CommitScene(Current.Scene!, new SceneEdit(Current.Revision + 1, prior.EntityId,
            prior.After, prior.Before, kind, prior.Revision)
        {
            AssetChange = SceneJournal.InvertAssetChange(prior.AssetChange),
            AppearanceChange = prior.AppearanceChange?.Reverse()
        });
        return true;
    }

    private bool ApplyAssetImport(SceneDocument scene, ImportSceneAsset operation)
    {
        if (Current.Assets.Any(item => item.Id == operation.Asset.Id) ||
            scene.History.Any(edit => edit.EntityId == operation.Asset.Id))
            throw new ProjectEditException(EditRejection.WrongTarget, "This asset ID already exists.");
        var catalog = Array.AsReadOnly(Current.Assets.Append(operation.Asset).OrderBy(item => item.Id).ToArray());
        PixelAsset.ValidateCatalog(catalog, Current.ProjectId, Current.SceneId);
        CommitScene(scene, new SceneEdit(Current.Revision + 1, operation.Asset.Id, null, null, LightEditKind.Edit, null)
            { AssetChange = new AssetPresence(true) }, catalog);
        return true;
    }

    private void CommitScene(SceneDocument scene, SceneEdit edit, IReadOnlyList<ProjectAsset>? catalog = null)
    {
        var next = WithSceneEdit(scene, edit, catalog);
        sceneJournal.Record(edit);
        Current = next;
    }

    private bool ApplyAppearance(SceneDocument scene, SceneOperation operation)
    {
        var current = scene.Appearance ?? new SceneAppearance(null, null, null);
        SceneAppearance update;
        if (operation is ConfigureRoomAppearance room)
        {
            if (room.EntityId != Current.SceneId)
                throw new ProjectEditException(EditRejection.WrongTarget, "This appearance edit belongs to another room.");
            var name = room.Title?.Trim();
            if (string.IsNullOrEmpty(name))
                throw new InvalidDataException("Enter a room name.");
            update = current with
            {
                Title = name == Current.Name ? null : name,
                BackgroundRgb = room.BackgroundRgb == SceneAppearance.DefaultBackgroundRgb ? null : room.BackgroundRgb
            };
        }
        else
        {
            var shift = (ShiftSceneLayer)operation;
            var ids = SceneAppearance.Ordered(scene.Entities, current.PaintOrder).Select(entity => entity.Id).ToList();
            var index = ids.IndexOf(shift.EntityId);
            if (index < 0)
                throw new ProjectEditException(EditRejection.MissingObject, "The object is no longer in the room.");
            var neighbor = index + (shift.Forward ? 1 : -1);
            if (neighbor < 0 || neighbor >= ids.Count)
                return false;
            (ids[index], ids[neighbor]) = (ids[neighbor], ids[index]);
            update = current with { PaintOrder = ids };
        }
        var next = SceneAppearance.Freeze(update);
        if (SceneAppearance.Same(current, next))
            return false;
        CommitScene(scene, new SceneEdit(Current.Revision + 1, Current.SceneId, null, null, LightEditKind.Edit, null)
        {
            AppearanceChange = new RoomAppearanceChange(scene.Appearance, next)
        });
        return true;
    }

    private MuseumProject WithSceneEdit(SceneDocument scene, SceneEdit edit, IReadOnlyList<ProjectAsset>? catalog = null)
    {
        if (scene.History.Count == SceneValidation.MaxEdits)
            throw new InvalidDataException("This scene has reached its edit history limit.");
        var entities = scene.Entities.Where(item => item.Id != edit.EntityId);
        if (edit.After is not null)
            entities = entities.Append(edit.After);
        var ordered = Array.AsReadOnly(entities.OrderBy(item => item.Id).ToArray());
        SceneValidation.ValidateEntities(ordered, Current.ProjectId, Current.SceneId);
        var active = (scene.ActiveAssetIds ?? []).ToHashSet();
        if (edit.AssetChange is { } change)
        {
            if (change.IsPresent)
                active.Add(edit.EntityId);
            else
                active.Remove(edit.EntityId);
        }
        SceneValidation.ValidateImages(ordered, active);
        var next = new SceneDocument(ordered, Array.AsReadOnly(scene.History.Append(edit).ToArray()))
        {
            ActiveAssetIds = active.Count == 0 ? null : Array.AsReadOnly(active.Order().ToArray()),
            Appearance = edit.AppearanceChange is { } appearance ? appearance.After : scene.Appearance
        };
        var schema = SceneValidation.RequiresSchemaFive(edit.After) || edit.AppearanceChange is not null
            ? MuseumProject.GameFormatVersion : Current.SchemaVersion;
        return Current with { Revision = edit.Revision, Scene = next, Assets = catalog ?? Current.Assets, SchemaVersion = schema };
    }
}
