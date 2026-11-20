namespace PlatzAtlas.Core;

public sealed record MuseumProject(
    int SchemaVersion,
    Guid ProjectId,
    Guid SceneId,
    string Name,
    long Revision,
    string Units,
    string Origin,
    IReadOnlyList<Lantern> Objects,
    IReadOnlyList<LightSwitch> Switches,
    IReadOnlyList<ProjectAsset> Assets,
    IReadOnlyList<LightEdit> History)
{
    public const int FormatVersion = 1;
    public const long InitialRevision = 1;
    public const string CoordinateUnits = "room-fraction";
    public const string CoordinateOrigin = "top-left, x-right, y-down";
}

public sealed record Lantern(Guid Id, string Name, string Location, double X, double Y, bool IsOn, Guid AssetId);

public sealed record LightSwitch(Guid Id, string Name, Guid TargetId);

public sealed record ProjectAsset(Guid Id, string Name, string MediaType, string ContentBase64, string Sha256);

public sealed record SetLight(Guid ProjectId, long BaseRevision, Guid ObjectId, bool IsOn, Guid? SwitchId = null);

public enum LightEditKind
{
    Edit,
    Undo,
    Redo
}

public sealed record LightEdit(
    long Revision,
    Guid ObjectId,
    bool WasOn,
    bool IsOn,
    LightEditKind Kind,
    long? ReversesRevision,
    Guid? SwitchId);

public enum EditRejection
{
    WrongProject,
    StaleRevision,
    WrongTarget,
    MissingObject
}

public sealed class ProjectEditException(EditRejection reason, string message) : InvalidOperationException(message)
{
    public EditRejection Reason { get; } = reason;
}
