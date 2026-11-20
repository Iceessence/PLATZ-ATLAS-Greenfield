namespace PlatzAtlas.Core;

// Studio owns a session on its dispatcher. ProjectStore serializes saves across editor processes.
public sealed class ProjectSession
{
    internal static readonly Guid PublicFixtureId = new("9e67d909-655c-489f-87c1-0dbbfe50d4b1");
    private readonly LightHistory history = new();
    private string? diskFingerprint;

    private ProjectSession(MuseumProject project, string? filePath = null, string? fingerprint = null)
    {
        foreach (var edit in project.History)
        {
            history.Record(edit);
        }

        Current = project;
        FilePath = filePath;
        diskFingerprint = fingerprint;
        SavedRevision = filePath is null ? null : project.Revision;
    }

    public MuseumProject Current { get; private set; }
    public string? FilePath { get; private set; }
    public long? SavedRevision { get; private set; }
    public bool IsDirty => Current.Revision != SavedRevision;
    public bool CanUndo => history.NextUndo is not null;
    public bool CanRedo => history.NextRedo is not null;
    public LightCommitReceipt? LatestAtlasCommit =>
        Current.History.LastOrDefault(edit => edit.AtlasCommit is not null) is { } last
            ? LightCommitReceipt.FromEdit(Current, last)
            : null;

    public LightCommitReceipt? FindAtlasCommit(Guid operationId) =>
        Current.History.FirstOrDefault(edit => edit.AtlasCommit?.OperationId == operationId) is { } match
            ? LightCommitReceipt.FromEdit(Current, match)
            : null;

    public static ProjectSession NewMuseum()
    {
        using var stream = typeof(ProjectSession).Assembly.GetManifestResourceStream("NightMuseum.platz")
            ?? throw new InvalidOperationException("The museum template is missing from this build.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var template = ProjectFormat.Read(buffer.ToArray());
        return new ProjectSession(template with { ProjectId = Guid.NewGuid() });
    }

    public static ProjectSession Open(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var bytes = File.ReadAllBytes(fullPath);
        try
        {
            var project = ProjectFormat.Read(bytes);
            return project.ProjectId == PublicFixtureId
                ? new ProjectSession(project with { ProjectId = Guid.NewGuid() })
                : new ProjectSession(project, fullPath, ProjectFormat.Digest(bytes));
        }
        catch (InvalidDataException exception) when (File.Exists(ProjectStore.PreviousPath(fullPath)))
        {
            throw new InvalidDataException(
                $"{exception.Message} A previous saved revision is available through Recover previous.", exception);
        }
    }

    public static ProjectSession OpenPrevious(string path)
    {
        var bytes = File.ReadAllBytes(ProjectStore.PreviousPath(Path.GetFullPath(path)));
        return new ProjectSession(ProjectFormat.Read(bytes));
    }

    public bool Apply(SetLight request)
    {
        var target = ValidateLightTarget(request);
        if (target.IsOn == request.IsOn)
            return false;

        Commit(new LightEdit(Current.Revision + 1, target.Id, target.IsOn, request.IsOn,
            LightEditKind.Edit, null, request.SwitchId));
        return true;
    }

    public LightPreview PreviewSavedLight(SetLight request)
    {
        var target = ValidateLightTarget(request);
        ReadSavedSnapshot();
        return new LightPreview(request, target, target with { IsOn = request.IsOn });
    }

    public bool Apply(SetSwitchBehavior request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CheckEditBase(request.ProjectId, request.BaseRevision);
        if (!Enum.IsDefined(request.Behavior))
            throw new ArgumentOutOfRangeException(nameof(request));
        var control = Current.Switches.SingleOrDefault(item => item.Id == request.SwitchId)
            ?? throw new ProjectEditException(EditRejection.WrongTarget, "This switch is no longer in the room.");
        var before = control.Behavior ?? SwitchBehavior.Toggle;
        if (before == request.Behavior)
            return false;
        var light = Current.Objects.Single(item => item.Id == control.TargetId);
        Commit(new LightEdit(Current.Revision + 1, light.Id, light.IsOn, light.IsOn, LightEditKind.Edit,
            null, control.Id) { BehaviorChange = new SwitchBehaviorChange(before, request.Behavior) });
        return true;
    }

    public MuseumProject ReadSavedSnapshot()
    {
        if (IsDirty || FilePath is null || diskFingerprint is null)
            throw new ProjectEditException(EditRejection.StaleRevision, "Save the current revision first.");
        if (!ProjectStore.HasSavedHead(FilePath, diskFingerprint))
            throw new ProjectEditException(EditRejection.StaleRevision, "The saved project changed on disk. Reopen it.");
        return Current;
    }

    // Only the trusted Studio host supplies the approval check. It runs again immediately before replacement.
    public LightCommitReceipt CommitApprovedLight(Guid operationId, SetLight request, Action requireApproval)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(requireApproval);
        if (operationId == Guid.Empty)
            throw new ArgumentException("A unique operation ID is required.", nameof(operationId));
        if (request.ProjectId != Current.ProjectId)
            throw new ProjectEditException(EditRejection.WrongProject, "This edit belongs to a different project.");
        if (request.ExpectedIsOn is not bool wasOn || request.SwitchId is not Guid switchId)
            throw new ProjectEditException(EditRejection.StaleValue, "The proposal needs an expected light state and switch.");
        if (IsDirty || FilePath is not { } path || diskFingerprint is not { } baseFingerprint)
            throw new ProjectEditException(EditRejection.StaleRevision, "Save the current revision before applying a proposal.");

        var payload = LightCommitReceipt.Digest(request.ProjectId, Current.SceneId, request.BaseRevision,
            request.ObjectId, switchId, wasOn, request.IsOn);
        var publication = ProjectStore.PublishApproved<ApprovedPublication>(path, requireApproval, existing =>
        {
            var disk = ProjectFormat.Read(existing);
            if (disk.ProjectId != Current.ProjectId || disk.SceneId != Current.SceneId)
                throw new ProjectEditException(EditRejection.WrongProject, "The project on disk has changed. Reopen it.");
            if (disk.History.FirstOrDefault(edit => edit.AtlasCommit?.OperationId == operationId) is { } prior)
            {
                var receipt = LightCommitReceipt.FromEdit(disk, prior);
                if (receipt.PayloadSha256 != payload)
                    throw new ProjectEditException(EditRejection.OperationIdConflict,
                        "This operation ID has already been used for a different light change.");
                return (new ApprovedPublication(receipt, null, null), null);
            }

            if (ProjectFormat.Digest(existing) != baseFingerprint)
                throw new ProjectEditException(EditRejection.StaleRevision, "The saved project changed on disk. Reopen it before applying the change.");
            var target = ValidateLightTarget(request);
            if (target.IsOn == request.IsOn)
                throw new ProjectEditException(EditRejection.StaleValue, "The light already has the proposed state.");

            var edit = new LightEdit(Current.Revision + 1, target.Id, wasOn, request.IsOn,
                LightEditKind.Edit, null, switchId) { AtlasCommit = new AtlasCommitStamp(operationId, payload) };
            var candidate = WithEdit(edit) with { SchemaVersion = Math.Max(Current.SchemaVersion, MuseumProject.ReceiptFormatVersion) };
            var bytes = ProjectFormat.Write(candidate);
            return (new ApprovedPublication(LightCommitReceipt.FromEdit(candidate, edit), candidate,
                ProjectFormat.Digest(bytes)), bytes);
        });

        if (publication.Project is { } saved)
        {
            history.Record(saved.History[^1]);
            Current = saved;
            SavedRevision = saved.Revision;
            diskFingerprint = publication.Fingerprint;
        }
        return publication.Receipt;
    }

    private Lantern ValidateLightTarget(SetLight request)
    {
        ArgumentNullException.ThrowIfNull(request);
        CheckEditBase(request.ProjectId, request.BaseRevision);

        if (request.SwitchId is Guid switchId &&
            !Current.Switches.Any(item => item.Id == switchId && item.TargetId == request.ObjectId))
        {
            throw new ProjectEditException(EditRejection.WrongTarget, "This switch controls a different lantern.");
        }

        var target = Current.Objects.FirstOrDefault(item => item.Id == request.ObjectId)
            ?? throw new ProjectEditException(EditRejection.MissingObject, "The selected lantern is no longer in this room.");
        if (request.ExpectedIsOn is bool expected && target.IsOn != expected)
        {
            throw new ProjectEditException(EditRejection.StaleValue, "The lantern’s current light state differs from the proposal.");
        }

        return target;
    }

    private void CheckEditBase(Guid projectId, long revision)
    {
        if (projectId != Current.ProjectId)
            throw new ProjectEditException(EditRejection.WrongProject, "This edit belongs to a different project.");
        if (revision != Current.Revision)
            throw new ProjectEditException(EditRejection.StaleRevision,
                $"This edit was based on revision {revision}. The room is at revision {Current.Revision}.");
    }

    public bool Undo() => Reverse(history.NextUndo, LightEditKind.Undo);

    public bool Redo() => Reverse(history.NextRedo, LightEditKind.Redo);

    public void Save(string? path = null, bool overwriteExisting = false)
    {
        var destination = Path.GetFullPath(path ?? FilePath
            ?? throw new InvalidOperationException("Choose a location for this project."));
        var sameFile = string.Equals(destination, FilePath, StringComparison.OrdinalIgnoreCase);
        var bytes = ProjectFormat.Write(Current);
        var fingerprint = ProjectFormat.Digest(bytes);
        var unchanged = sameFile && !IsDirty;
        ProjectStore.Publish(destination, bytes, sameFile ? diskFingerprint : null, overwriteExisting, unchanged);
        FilePath = destination;
        if (!unchanged)
        {
            diskFingerprint = fingerprint;
        }

        SavedRevision = Current.Revision;
    }

    private bool Reverse(LightEdit? prior, LightEditKind kind)
    {
        if (prior is null)
        {
            return false;
        }

        Commit(new LightEdit(Current.Revision + 1, prior.ObjectId, prior.IsOn, prior.WasOn,
            kind, prior.Revision, prior.SwitchId)
        {
            BehaviorChange = prior.BehaviorChange is { } change ? new SwitchBehaviorChange(change.After, change.Before) : null
        });
        return true;
    }

    private void Commit(LightEdit edit)
    {
        var next = WithEdit(edit);
        history.Record(edit);
        Current = next;
    }

    private MuseumProject WithEdit(LightEdit edit)
    {
        var next = Current with
        {
            Revision = edit.Revision,
            History = Array.AsReadOnly(Current.History.Append(edit).ToArray())
        };
        if (edit.BehaviorChange is { } behavior)
            return next with
            {
                SchemaVersion = MuseumProject.FormatVersion,
                Switches = Array.AsReadOnly(Current.Switches.Select(item => item.Id == edit.SwitchId
                    ? item with { Behavior = behavior.After == SwitchBehavior.Toggle ? null : behavior.After }
                    : item).ToArray())
            };

        return next with { Objects = Array.AsReadOnly(Current.Objects.Select(item => item.Id == edit.ObjectId
            ? item with { IsOn = edit.IsOn } : item).ToArray()) };
    }

    private sealed record ApprovedPublication(LightCommitReceipt Receipt, MuseumProject? Project, string? Fingerprint);
}
