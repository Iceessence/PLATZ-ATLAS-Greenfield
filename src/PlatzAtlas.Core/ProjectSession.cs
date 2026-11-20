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
        ArgumentNullException.ThrowIfNull(request);

        if (request.ProjectId != Current.ProjectId)
        {
            throw new ProjectEditException(EditRejection.WrongProject, "This edit belongs to a different project.");
        }

        if (request.BaseRevision != Current.Revision)
        {
            throw new ProjectEditException(EditRejection.StaleRevision,
                $"This edit was based on revision {request.BaseRevision}. The room is at revision {Current.Revision}.");
        }

        if (request.SwitchId is Guid switchId &&
            !Current.Switches.Any(item => item.Id == switchId && item.TargetId == request.ObjectId))
        {
            throw new ProjectEditException(EditRejection.WrongTarget, "This switch controls a different lantern.");
        }

        var target = Current.Objects.FirstOrDefault(item => item.Id == request.ObjectId)
            ?? throw new ProjectEditException(EditRejection.MissingObject, "The selected lantern is no longer in this room.");
        if (target.IsOn == request.IsOn)
        {
            return false;
        }

        Commit(new LightEdit(Current.Revision + 1, target.Id, target.IsOn, request.IsOn,
            LightEditKind.Edit, null, request.SwitchId));
        return true;
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
            kind, prior.Revision, prior.SwitchId));
        return true;
    }

    private void Commit(LightEdit edit)
    {
        var lights = Current.Objects.Select(item =>
            item.Id == edit.ObjectId ? item with { IsOn = edit.IsOn } : item).ToArray();
        var edits = Current.History.Append(edit).ToArray();
        var next = Current with
        {
            Revision = edit.Revision,
            Objects = Array.AsReadOnly(lights),
            History = Array.AsReadOnly(edits)
        };
        history.Record(edit);
        Current = next;
    }
}
