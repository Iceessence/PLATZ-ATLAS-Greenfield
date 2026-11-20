namespace PlatzAtlas.Atlas;

public readonly record struct ProjectReadGrant(Guid Id, ProjectReadScope Scope);
public sealed record SwitchQuestion(int ProfileVersion, ProjectReadGrant Grant, string Text);

public enum QuestionStatus
{
    Answered,
    AccessDenied,
    UnsupportedQuestion,
    IncompatibleProfile,
    InvalidSnapshot,
    EvidenceUnavailable
}

public sealed record SwitchAnswer(ProjectReadScope Scope, SwitchSource Switch, LanternSource Lantern, string SnapshotSha256);
public sealed record SwitchQuestionResult(QuestionStatus Status, SwitchAnswer? Answer = null);

public sealed record SwitchLightProposal(
    ProjectReadScope Scope, Guid SwitchId, Guid ObjectId, bool ExpectedIsOn, bool IsOn);

public enum PreviewStatus
{
    Staged,
    AccessDenied,
    StaleBase,
    EvidenceUnavailable
}

// The trusted host owns the grant and the evidence used for a detached preview.
public sealed class ProjectQuestionHost
{
    public const string SupportedQuestion = "Which lantern does the west switch control?";
    public const int MaxQuestionLength = 160;

    private readonly object _sync = new();
    private ProjectReadGrant? _grant;
    private Func<ReadOnlyMemory<byte>>? _readSnapshot;
    private SwitchAnswer? _latestAnswer;

    public ProjectReadGrant Allow(ProjectReadScope scope, Func<ReadOnlyMemory<byte>> readSnapshot)
    {
        if (!scope.IsValid)
            throw new ArgumentException("A project, scene and saved revision are required.", nameof(scope));
        ArgumentNullException.ThrowIfNull(readSnapshot);

        lock (_sync)
        {
            _readSnapshot = readSnapshot;
            _latestAnswer = null;
            var grant = new ProjectReadGrant(Guid.NewGuid(), scope);
            _grant = grant;
            return grant;
        }
    }

    public void Revoke()
    {
        lock (_sync)
        {
            _grant = null;
            _readSnapshot = null;
            _latestAnswer = null;
        }
    }

    public SwitchQuestionResult Ask(SwitchQuestion request)
    {
        lock (_sync)
        {
            // Keep the capability check and the bounded synchronous source read in the same critical section.
            if (_grant != request.Grant || _readSnapshot is null)
                return new(QuestionStatus.AccessDenied);
            _latestAnswer = null;
            if (request.ProfileVersion != SwitchEvidenceProfile.Version)
                return new(QuestionStatus.IncompatibleProfile);
            if (request.Text is not { Length: <= MaxQuestionLength } text
                || !string.Equals(text.Trim().TrimEnd('?'), SupportedQuestion[..^1], StringComparison.OrdinalIgnoreCase))
                return new(QuestionStatus.UnsupportedQuestion);

            var result = ReadAuthorizedAnswer(request.Grant.Scope, _readSnapshot);
            // A trusted source can cancel reentrantly. Discard any result after the grant changes.
            if (_grant != request.Grant)
                return new(QuestionStatus.AccessDenied);
            _latestAnswer = result.Answer;
            return result;
        }
    }

    public (PreviewStatus Status, T? Draft) StagePreview<T>(
        ProjectReadGrant grant, ProjectReadScope current, SwitchAnswer answer, Func<SwitchLightProposal, T> stage)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(stage);
        lock (_sync)
        {
            if (_grant != grant || current.ProjectId != grant.Scope.ProjectId || current.SceneId != grant.Scope.SceneId)
                return (PreviewStatus.AccessDenied, null);
            if (current.Revision != grant.Scope.Revision)
                return (PreviewStatus.StaleBase, null);
            if (_latestAnswer is null || !ReferenceEquals(_latestAnswer, answer))
                return (PreviewStatus.EvidenceUnavailable, null);

            var proposal = new SwitchLightProposal(current, answer.Switch.Id, answer.Lantern.Id,
                answer.Lantern.IsOn, !answer.Lantern.IsOn);
            var draft = stage(proposal);
            return _grant == grant && ReferenceEquals(_latestAnswer, answer)
                ? (PreviewStatus.Staged, draft)
                : (PreviewStatus.AccessDenied, null);
        }
    }

    private static SwitchQuestionResult ReadAuthorizedAnswer(
        ProjectReadScope scope, Func<ReadOnlyMemory<byte>> readSnapshot)
    {
        try
        {
            var (status, snapshot, checksum) = SwitchEvidenceProfile.Read(readSnapshot());
            if (snapshot is null || checksum is null)
                return new(status);
            if (snapshot.Scope != scope)
                return new(QuestionStatus.AccessDenied);

            var switches = snapshot.Switches
                .Where(item => string.Equals(item.Name, "West Switch", StringComparison.OrdinalIgnoreCase))
                .Take(2).ToArray();
            if (switches.Length != 1)
                return new(QuestionStatus.EvidenceUnavailable);

            var selectedSwitch = switches[0];
            var lantern = snapshot.Objects.SingleOrDefault(item => item.Id == selectedSwitch.TargetId);
            return lantern is null
                ? new(QuestionStatus.EvidenceUnavailable)
                : new(QuestionStatus.Answered, new SwitchAnswer(scope, selectedSwitch, lantern, checksum));
        }
        catch (InvalidDataException)
        {
            return new(QuestionStatus.InvalidSnapshot);
        }
    }
}
