namespace PlatzAtlas.Core;

public sealed record WorkingScenePlan(Guid WorkspaceId, Guid ProjectId, Guid SceneId, long BaseRevision,
    IReadOnlyList<SceneOperation> Operations, IReadOnlyList<SceneEdit> Edits, IReadOnlyList<SceneEntity> Entities);

public sealed record ScenePlanReceipt(Guid OperationId, Guid ProjectId, Guid SceneId, long BaseRevision, long Revision,
    int Changes, string Sha256);

public sealed partial class ProjectSession
{
    public ScenePlanReceipt? LatestScenePlan
    {
        get
        {
            var history = Current.Scene?.History;
            var lead = history?.LastOrDefault(edit => edit is { Kind: LightEditKind.Edit, Group.Index: 0 });
            return lead is null ? null : PlanReceipt(history!.Skip(checked((int)(lead.Revision - 2))).Take(lead.Group!.Count).ToArray());
        }
    }

    public WorkingScenePlan PreviewWorkingPlan(IReadOnlyList<SceneOperation> operations)
    {
        var before = Current;
        var staged = StagePlan(operations);
        return new WorkingScenePlan(WorkspaceId, before.ProjectId, before.SceneId, before.Revision,
            Array.AsReadOnly(operations.ToArray()), Array.AsReadOnly(staged.Scene!.History.Skip(before.Scene!.History.Count).ToArray()),
            staged.Scene.Entities);
    }

    public ScenePlanReceipt ApplyApprovedWorkingPlan(Guid operationId, WorkingScenePlan preview, Action requireApproval)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(requireApproval);
        if (preview.WorkspaceId != WorkspaceId || preview.SceneId != Current.SceneId || Current.Scene is not { } scene)
            throw new ProjectEditException(EditRejection.WrongProject, "This plan belongs to another open room.");
        if (operationId == Guid.Empty || scene.History.Any(edit => edit.Group?.Id == operationId || edit.AtlasCommit?.OperationId == operationId))
            throw new ProjectEditException(EditRejection.OperationIdConflict, "The plan needs a new approval ID.");
        requireApproval();
        CheckEditBase(preview.ProjectId, preview.BaseRevision);
        var before = Current;
        var staged = StagePlan(preview.Operations);
        var edits = staged.Scene!.History.Skip(before.Scene!.History.Count).ToArray();
        if (!edits.SequenceEqual(preview.Edits) || !staged.Scene.Entities.SequenceEqual(preview.Entities))
            throw new ProjectEditException(EditRejection.StaleValue, "The staged plan differs from the reviewed preview.");
        var grouped = edits.Select((edit, index) => edit with { Group = new SceneEditGroup(operationId, index, edits.Length) }).ToArray();
        var next = staged with
        {
            Scene = staged.Scene with { History = Array.AsReadOnly(before.Scene.History.Concat(grouped).ToArray()) }
        };
        var receipt = PlanReceipt(grouped);
        requireApproval();
        if (!ReferenceEquals(before, Current))
            throw new ProjectEditException(EditRejection.StaleRevision, "The working room changed during approval.");
        foreach (var edit in grouped)
            sceneJournal.Record(edit);
        Current = next;
        return receipt;
    }

    private MuseumProject StagePlan(IReadOnlyList<SceneOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        if (Current.Scene is null || operations.Count is < 2 or > SceneEditGroup.MaxOperations)
            throw new InvalidDataException("Plans require an original room and 2–12 supported changes.");
        var staged = new ProjectSession(Current);
        foreach (var operation in operations)
        {
            if (operation is not (AddSceneEntity or ConfigureSceneSwitch or TransformSceneEntity or SetSceneDoor or SetSceneLamp or SetSceneGoal))
                throw new InvalidDataException("A plan contains an operation outside the working-room profile.");
            if (!staged.Apply(operation))
                throw new ProjectEditException(EditRejection.StaleValue, "A step would leave the room unchanged.");
        }
        return staged.Current;
    }

    private ScenePlanReceipt PlanReceipt(SceneEdit[] group) => new(group[0].Group!.Id, Current.ProjectId, Current.SceneId,
        group[0].Revision - 1, group[^1].Revision, group.Length,
        ProjectFormat.ScenePlanDigest(Current.ProjectId, Current.SceneId, group));

    private bool ReverseSceneGroup(SceneEdit prior, LightEditKind kind)
    {
        var staged = new ProjectSession(Current);
        var group = prior.Group!;
        for (var count = 0; count < group.Count; count++)
        {
            var next = kind == LightEditKind.Undo ? staged.sceneJournal.NextUndo : staged.sceneJournal.NextRedo;
            if (next?.Group?.Id != group.Id || next.Group.Count != group.Count)
                throw new InvalidDataException("The grouped edit history is incomplete.");
            staged.ReverseSingleScene(next, kind);
        }
        foreach (var edit in staged.Current.Scene!.History.Skip(Current.Scene!.History.Count))
            sceneJournal.Record(edit);
        Current = staged.Current;
        return true;
    }
}
