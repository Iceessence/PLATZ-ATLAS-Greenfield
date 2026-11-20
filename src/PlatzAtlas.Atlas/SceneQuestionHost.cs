using System.Globalization;

namespace PlatzAtlas.Atlas;

public enum SceneProposalKind { Rename, Move, SetState, Resize, Rotate, Wire, Unlink, Add, Duplicate }
public sealed record SceneProposal(SceneProposalKind Kind, string? Name = null, double X = 0, double Y = 0, bool State = false)
{
    public Guid? TargetId { get; init; }
    public SceneSourceKind? ObjectKind { get; init; }
}
public sealed record SceneAnswer(ProjectReadScope Scope, SceneSource? Source, SceneSource? Target, string Sha256, SceneProposal? Proposal)
{
    public SceneSource? AdditionalTarget { get; init; }
    public SceneSource? ProposedTarget { get; init; }
}
public sealed record SceneQuestionResult(QuestionStatus Status, SceneAnswer? Answer = null, string? Message = null)
{
    public SceneHealthReport? Health { get; init; }
}
public sealed record SceneWriteReview(Guid Id, ProjectReadGrant Grant, SceneAnswer Answer);

public sealed class SceneQuestionHost(TimeProvider? clock = null)
{
    public const int MaxQuestionLength = 220;
    public static readonly TimeSpan ReviewLifetime = TimeSpan.FromMinutes(1);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _sync = new();
    private ProjectReadGrant? _grant;
    private Func<ReadOnlyMemory<byte>>? _source;
    private SceneAnswer? _latest;
    private SceneWriteReview? _review;
    private long _reviewStarted;

    public ProjectReadGrant Allow(ProjectReadScope scope, Func<ReadOnlyMemory<byte>> source)
    {
        if (!scope.IsValid)
            throw new ArgumentException("The grant requires a valid saved scene revision.", nameof(scope));
        ArgumentNullException.ThrowIfNull(source);
        lock (_sync)
        {
            var grant = new ProjectReadGrant(Guid.NewGuid(), scope);
            _grant = grant;
            _source = source;
            _latest = null;
            _review = null;
            return grant;
        }
    }

    public void Revoke()
    {
        lock (_sync)
        {
            _grant = null;
            _source = null;
            _latest = null;
            _review = null;
        }
    }

    public SceneQuestionResult Ask(ProjectReadGrant grant, int version, Guid? selected, string text)
    {
        lock (_sync)
        {
            if (_grant != grant || _source is null)
                return new(QuestionStatus.AccessDenied);
            _latest = null;
            _review = null;
            if (version != SceneEvidenceProfile.Version)
                return new(QuestionStatus.IncompatibleProfile);
            if (text is not { Length: <= MaxQuestionLength } || Parse(text) is not { } query)
                return new(QuestionStatus.UnsupportedQuestion, Message:
                    "Try Inspect selection, Check scene, or Move <name> to 0.5, 0.4. Write changes require explicit preview and confirmation.");

            SceneEvidence evidence;
            string sha;
            try { (evidence, sha) = SceneEvidenceProfile.Open(_source().Span); }
            catch (InvalidDataException error) { return new(QuestionStatus.InvalidSnapshot, Message: error.Message); }
            if (_grant != grant || evidence.Scope != grant.Scope)
                return new(QuestionStatus.AccessDenied);
            if (query.Criterion == "scene-health")
                return new(QuestionStatus.Answered) { Health = SceneHealthReport.Inspect(evidence, sha) };
            var proposal = query.Proposal;
            if (proposal?.Kind == SceneProposalKind.Add)
            {
                _latest = new SceneAnswer(grant.Scope, null, null, sha, proposal);
                return new(QuestionStatus.Answered, _latest);
            }
            var candidates = SceneSourceResolver.Resolve(evidence, query.Name, selected);
            if (candidates.Count != 1)
                return Missing(candidates.Count);

            var source = candidates[0];
            if (query.Proposal is { Kind: SceneProposalKind.SetState } &&
                ((query.Criterion == "door" && source.Kind != SceneSourceKind.Door) ||
                 (query.Criterion == "lamp" && source.Kind != SceneSourceKind.Lamp)))
                return new(QuestionStatus.UnsupportedQuestion, Message: "Use open or closed for a door, and on or off for a lamp.");
            if ((query.Criterion == "control" || proposal?.Kind is SceneProposalKind.Wire or SceneProposalKind.Unlink)
                && source.Kind != SceneSourceKind.Switch)
                return new(QuestionStatus.UnsupportedQuestion, Message: "Choose a switch to inspect its link.");
            if (proposal?.Kind == SceneProposalKind.Duplicate && source.Kind == SceneSourceKind.Spawn)
                return new(QuestionStatus.UnsupportedQuestion, Message: "A room can have only one spawn.");
            SceneSource? proposedTarget = null;
            if (proposal is { Kind: SceneProposalKind.Wire, Name: { } requested })
            {
                var destinations = SceneSourceResolver.Resolve(evidence, requested, selected);
                if (destinations.Count != 1)
                    return Missing(destinations.Count);
                proposedTarget = destinations[0];
                if (proposedTarget.Kind is not (SceneSourceKind.Door or SceneSourceKind.Lamp))
                    return new(QuestionStatus.UnsupportedQuestion, Message: "Wire a switch to a door or lamp.");
                proposal = proposal with { TargetId = proposedTarget.Id };
            }
            var target = source.TargetId is { } linked ? evidence.Objects.Single(item => item.Id == linked) : null;
            _latest = new SceneAnswer(grant.Scope, source, target, sha, proposal)
            {
                AdditionalTarget = source.SecondTargetId is Guid second ? evidence.Objects.Single(item => item.Id == second) : null,
                ProposedTarget = proposedTarget
            };
            return new(QuestionStatus.Answered, _latest);
        }
    }

    private static SceneQuestionResult Missing(int count) => new(QuestionStatus.EvidenceUnavailable,
        Message: count > 1 ? "More than one object has that name. Select one or use its exact ID."
            : "There is no matching object in the authorized revision.");

    public SceneWriteReview BeginReview(ProjectReadGrant grant, SceneAnswer answer, ProjectReadScope current)
    {
        lock (_sync)
        {
            if (_grant != grant || current != grant.Scope || !ReferenceEquals(answer, _latest) || answer.Proposal is null)
                throw new InvalidOperationException("Ask for an edit in the currently authorized revision first.");
            var review = new SceneWriteReview(Guid.NewGuid(), grant, answer);
            _review = review;
            _reviewStarted = _clock.GetTimestamp();
            return review;
        }
    }

    public void DiscardReview()
    {
        lock (_sync)
            _review = null;
    }

    public T Confirm<T>(SceneWriteReview review, ProjectReadScope current, Func<Guid, Action, T> publish)
    {
        lock (_sync)
        {
            RequireApproval();
            var result = publish(review.Id, RequireApproval);
            _review = null;
            _latest = null;
            return result;
        }

        void RequireApproval()
        {
            if (_grant != review.Grant || !ReferenceEquals(_review, review) || current != review.Grant.Scope)
                throw new InvalidOperationException("Write approval is no longer valid. Review the change again.");
            if (_clock.GetElapsedTime(_reviewStarted) >= ReviewLifetime)
                throw new InvalidOperationException("The one-minute review expired. Preview the change again.");
        }
    }

    private static ParsedQuestion? Parse(string text)
    {
        var input = text.Trim().TrimEnd('?').Trim();
        if (input.Equals("Check scene", StringComparison.OrdinalIgnoreCase) ||
            input.Equals("Scene health", StringComparison.OrdinalIgnoreCase))
            return new("", Criterion: "scene-health");
        if (Strip("Inspect ") is { Length: > 0 } inspect)
            return new(inspect);
        if (Strip("Describe ") is { Length: > 0 } describe)
            return new(describe);
        if (Strip("Where is ") is { Length: > 0 } where)
            return new(where);
        if (Strip("What does ") is { } controls && controls.EndsWith(" control", StringComparison.OrdinalIgnoreCase))
            return new(controls[..^8], Criterion: "control");
        if (Strip("What state is ") is { Length: > 0 } state)
            return new(state);
        if (Strip("Move ") is { } move && Split(move, " to ") is { } movement &&
            Pair(movement.Value, 0) is { } point)
            return new(movement.Name, new(SceneProposalKind.Move, X: point.X, Y: point.Y));
        if (Strip("Resize ") is { } resize && Split(resize, " to ") is { } dimensions &&
            Pair(dimensions.Value, .025) is { } size)
            return new(dimensions.Name, new(SceneProposalKind.Resize, X: size.X, Y: size.Y));
        if (Strip("Rotate ") is { } rotate && Split(rotate, " to ") is { } angle &&
            Number(angle.Value, -360, 360, out var degrees))
            return new(angle.Name, new(SceneProposalKind.Rotate, X: degrees));
        if (Strip("Wire ") is { } wire && Split(wire, " to ") is { } link)
            return new(link.Name, new(SceneProposalKind.Wire, Name: link.Value));
        if (Strip("Unlink ") is { Length: > 0 } unlink)
            return new(unlink, new(SceneProposalKind.Unlink));
        if (Strip("Duplicate ") is { Length: > 0 } duplicate)
            return new(duplicate, new(SceneProposalKind.Duplicate));
        if (Strip("Add ") is { } add && Split(add, " at ") is { } placement &&
            Split(placement.Name, " named ") is { } named && named.Value is { Length: <= 96 } &&
            !named.Value.Any(char.IsControl) &&
            Enum.TryParse<SceneSourceKind>(named.Name, true, out var kind) &&
            kind is SceneSourceKind.Wall or SceneSourceKind.Lamp or SceneSourceKind.Door or SceneSourceKind.Switch
                or SceneSourceKind.Spawn or SceneSourceKind.Key or SceneSourceKind.Goal &&
            string.Equals(named.Name, kind.ToString(), StringComparison.OrdinalIgnoreCase) &&
            Pair(placement.Value, 0) is { } position)
            return new("", new SceneProposal(SceneProposalKind.Add, named.Value, position.X, position.Y) { ObjectKind = kind });
        if (Strip("Rename ") is { } rename && Split(rename, " to ") is { } newName &&
            newName.Value is { Length: > 0 and <= 96 } && !newName.Value.Any(char.IsControl))
            return new(newName.Name, new(SceneProposalKind.Rename, Name: newName.Value));
        if (Strip("Set ") is { } set && Split(set, " ") is { } value)
        {
            return value.Value.ToLowerInvariant() switch
            {
                "open" => new(value.Name, new(SceneProposalKind.SetState, State: true), "door"),
                "closed" => new(value.Name, new(SceneProposalKind.SetState, State: false), "door"),
                "on" => new(value.Name, new(SceneProposalKind.SetState, State: true), "lamp"),
                "off" => new(value.Name, new(SceneProposalKind.SetState, State: false), "lamp"),
                _ => null
            };
        }
        return null;

        string? Strip(string prefix) => input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? input[prefix.Length..].Trim() : null;
    }

    private static (double X, double Y)? Pair(string input, double minimum) =>
        input.Split(',', StringSplitOptions.TrimEntries) is [var x, var y] &&
        Number(x, minimum, 1, out var first) && Number(y, minimum, 1, out var second)
            ? (first, second) : null;

    private static bool Number(string text, double minimum, double maximum, out double number) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && number >= minimum && number <= maximum;

    private static (string Name, string Value)? Split(string input, string separator)
    {
        var index = input.LastIndexOf(separator, StringComparison.OrdinalIgnoreCase);
        if (index <= 0 || (index + separator.Length) >= input.Length)
            return null;
        var name = input[..index].Trim();
        var value = input[(index + separator.Length)..].Trim();
        return name.Length == 0 || value.Length == 0 ? null : (name, value);
    }

    private sealed record ParsedQuestion(string Name, SceneProposal? Proposal = null, string? Criterion = null);
}
