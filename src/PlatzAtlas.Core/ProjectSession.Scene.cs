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
        var prior = scene.Entities.SingleOrDefault(entity => entity.Id == operation.EntityId);
        if (operation is AddSceneEntity && (prior is not null || operation.EntityId == Current.ProjectId ||
            operation.EntityId == Current.SceneId || scene.History.Any(edit => edit.EntityId == operation.EntityId)))
            throw new ProjectEditException(EditRejection.WrongTarget, "This object ID is already in the scene.");
        if (operation is not AddSceneEntity && prior is null)
            throw new ProjectEditException(EditRejection.MissingObject, "This object is no longer in the scene.");
        SceneEntity? next = operation switch
        {
            AddSceneEntity add => add.Entity,
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
                prior with { Component = new SceneSwitch(control.TargetId, control.Behavior) },
            _ => throw new ProjectEditException(EditRejection.WrongTarget, "This edit does not apply to the object.")
        };
        if (prior == next)
            return false;
        CommitScene(scene, new SceneEdit(Current.Revision + 1, operation.EntityId, prior, next, LightEditKind.Edit, null));
        return true;
    }

    private bool ReverseScene(SceneEdit? prior, LightEditKind kind)
    {
        if (prior is null)
            return false;
        CommitScene(Current.Scene!, new SceneEdit(Current.Revision + 1, prior.EntityId,
            prior.After, prior.Before, kind, prior.Revision) { AssetChange = SceneJournal.InvertAssetChange(prior.AssetChange) });
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
            ActiveAssetIds = active.Count == 0 ? null : Array.AsReadOnly(active.Order().ToArray())
        };
        sceneJournal.Record(edit);
        Current = Current with { Revision = edit.Revision, Scene = next, Assets = catalog ?? Current.Assets };
    }
}
