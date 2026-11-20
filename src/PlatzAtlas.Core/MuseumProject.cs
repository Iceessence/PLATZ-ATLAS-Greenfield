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
    public const int LegacyFormatVersion = 1;
    public const int ReceiptFormatVersion = 2;
    public const int FormatVersion = 3;
    public const long InitialRevision = 1;
    public const string CoordinateUnits = "room-fraction";
    public const string CoordinateOrigin = "top-left, x-right, y-down";
}

public sealed record Lantern(Guid Id, string Name, string Location, double X, double Y, bool IsOn, Guid AssetId);

public enum SwitchBehavior { Toggle, TurnOn }

public sealed record LightSwitch(Guid Id, string Name, Guid TargetId)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SwitchBehavior? Behavior { get; init; }
}

public sealed record SetSwitchBehavior(Guid ProjectId, long BaseRevision, Guid SwitchId, SwitchBehavior Behavior);
public sealed record SwitchBehaviorChange(SwitchBehavior Before, SwitchBehavior After);

public sealed record ProjectAsset(Guid Id, string Name, string MediaType, string ContentBase64, string Sha256);

public sealed record SetLight(Guid ProjectId, long BaseRevision, Guid ObjectId, bool IsOn, Guid? SwitchId = null)
{
    public bool? ExpectedIsOn { get; init; }
}

public sealed record LightPreview(SetLight Operation, Lantern Before, Lantern After);

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
    Guid? SwitchId)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public AtlasCommitStamp? AtlasCommit { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public SwitchBehaviorChange? BehaviorChange { get; init; }
}

public enum EditRejection
{
    WrongProject,
    StaleRevision,
    WrongTarget,
    MissingObject,
    StaleValue,
    OperationIdConflict
}

public sealed class ProjectEditException(EditRejection reason, string message) : InvalidOperationException(message)
{
    public EditRejection Reason { get; } = reason;
}
