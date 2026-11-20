namespace PlatzAtlas.Core;

public sealed record SceneNumber(Guid Id, long Number);

public static class SceneNumbers
{
    public static IReadOnlyDictionary<Guid, long> FromHistory(SceneDocument scene) => scene.History
        .Where(edit => edit.Kind == LightEditKind.Edit && edit.Before is null && (edit.After is not null || edit.AssetChange is { IsPresent: true }))
        .ToDictionary(edit => edit.EntityId, edit => edit.Revision);
}
