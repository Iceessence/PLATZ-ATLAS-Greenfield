namespace PlatzAtlas.Core;

public sealed record ScenePreview(SceneOperation Operation, SceneEntity? Before, SceneEntity After)
{
    public Guid? WorkspaceId { get; internal init; }
}
public sealed record SceneCommitStamp(Guid OperationId, string PayloadSha256);
public sealed record SceneCommitReceipt(Guid OperationId, Guid ProjectId, Guid SceneId, Guid EntityId,
    long BaseRevision, long Revision, string PayloadSha256);

public sealed partial class ProjectSession
{
    public SceneCommitReceipt? LatestSceneCommit =>
        Current.Scene?.History.LastOrDefault(edit => edit.AtlasCommit is not null) is { } edit
            ? SceneReceipt(Current, edit) : null;

    public ScenePreview PreviewSavedScene(SceneOperation operation) =>
        PreviewScene(ReadSavedSnapshot(), operation);

    public ScenePreview PreviewWorkingScene(SceneOperation operation) =>
        PreviewScene(Current, operation) with { WorkspaceId = WorkspaceId };

    private ScenePreview PreviewScene(MuseumProject project, SceneOperation operation)
    {
        if (operation is not (RenameSceneEntity or TransformSceneEntity or SetSceneLamp or SetSceneDoor or ConfigureSceneSwitch
            or AddSceneEntity or DuplicateSceneEntity))
            throw new ProjectEditException(EditRejection.WrongTarget, "This operation is outside the scene proposal profile.");
        CheckEditBase(operation.ProjectId, operation.BaseRevision);
        var scene = project.Scene ?? throw new ProjectEditException(EditRejection.WrongTarget, "Open an original room.");
        var edit = PlanSceneEdit(scene, operation) ?? throw new ProjectEditException(EditRejection.StaleValue, "The object already has that value.");
        WithSceneEdit(scene, edit);
        return new ScenePreview(operation, edit.Before, edit.After!);
    }

    public SceneCommitReceipt ApplyApprovedWorkingScene(Guid operationId, ScenePreview preview, Action requireApproval)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(requireApproval);
        if (preview.WorkspaceId != WorkspaceId)
            throw new ProjectEditException(EditRejection.WrongProject, "This preview belongs to another open workspace.");
        requireApproval();
        var before = Current;
        if (PreviewWorkingScene(preview.Operation) != preview)
            throw new ProjectEditException(EditRejection.StaleValue, "The working room no longer matches its preview.");
        var scene = before.Scene!;
        var edit = MakeApprovedSceneEdit(operationId, scene, preview);
        var next = WithSceneEdit(scene, edit);
        requireApproval();
        if (!ReferenceEquals(before, Current))
            throw new ProjectEditException(EditRejection.StaleRevision, "The working room changed during approval.");
        sceneJournal.Record(edit);
        Current = next;
        return SceneReceipt(next, edit);
    }

    // Studio owns the revocable approval and rechecks it inside the project writer.
    public SceneCommitReceipt CommitApprovedScene(Guid operationId, ScenePreview preview, Action requireApproval)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(requireApproval);
        if (IsDirty || FilePath is not { } path || diskFingerprint is not { } fingerprint)
            throw new ProjectEditException(EditRejection.StaleRevision, "Save the current room before confirming an edit.");
        var publication = ProjectStore.PublishApproved<(MuseumProject Project, SceneEdit Edit, string Fingerprint)>(
            path, requireApproval, existing =>
            {
                if (ProjectFormat.Digest(existing) != fingerprint)
                    throw new ProjectEditException(EditRejection.StaleRevision, "The saved project changed on disk. Reopen it.");
                if (PreviewSavedScene(preview.Operation) != preview)
                    throw new ProjectEditException(EditRejection.StaleValue, "The proposal no longer matches its preview.");
                var scene = Current.Scene!;
                var edit = MakeApprovedSceneEdit(operationId, scene, preview);
                var next = WithSceneEdit(scene, edit);
                var bytes = ProjectFormat.Write(next);
                return ((next, edit, ProjectFormat.Digest(bytes)), bytes);
            });
        sceneJournal.Record(publication.Edit);
        Current = publication.Project;
        SavedRevision = Current.Revision;
        diskFingerprint = publication.Fingerprint;
        return SceneReceipt(Current, publication.Edit);
    }

    private SceneEdit MakeApprovedSceneEdit(Guid operationId, SceneDocument scene, ScenePreview preview)
    {
        if (operationId == Guid.Empty)
            throw new ArgumentException("The proposal needs an operation ID.", nameof(operationId));
        if (scene.History.Any(edit => edit.AtlasCommit?.OperationId == operationId))
            throw new ProjectEditException(EditRejection.OperationIdConflict, "This proposal has already been used.");
        var edit = new SceneEdit(Current.Revision + 1, preview.After.Id, preview.Before, preview.After, LightEditKind.Edit, null);
        return edit with { AtlasCommit = new SceneCommitStamp(operationId,
            ProjectFormat.SceneEditDigest(Current.ProjectId, Current.SceneId, edit)) };
    }

    private static SceneCommitReceipt SceneReceipt(MuseumProject project, SceneEdit edit) =>
        new(edit.AtlasCommit!.OperationId, project.ProjectId, project.SceneId, edit.EntityId,
            edit.Revision - 1, edit.Revision, edit.AtlasCommit.PayloadSha256);
}
