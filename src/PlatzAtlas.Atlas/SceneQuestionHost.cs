using System.Globalization;

namespace PlatzAtlas.Atlas;

public enum SceneProposalKind { Rename, Move, SetState }
public sealed record SceneProposal(SceneProposalKind Kind, string? Name = null, double X = 0, double Y = 0, bool State = false);
public sealed record SceneAnswer(ProjectReadScope Scope, SceneSource Source, SceneSource? Target, string Sha256, SceneProposal? Proposal);
public sealed record SceneQuestionResult(QuestionStatus Status, SceneAnswer? Answer = null, string? Message = null);
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
                    "Try Inspect selection, Where is <name>?, What does <switch> control?, or Move <name> to 0.5, 0.4.");

            SceneEvidence evidence;
            string sha;
            try { (evidence, sha) = SceneEvidenceProfile.Open(_source().Span); }
            catch (InvalidDataException error) { return new(QuestionStatus.InvalidSnapshot, Message: error.Message); }
            if (_grant != grant || evidence.Scope != grant.Scope)
                return new(QuestionStatus.AccessDenied);
            var request = query.Name.Trim();
            var candidates = (Guid.TryParse(request, out var id)
                ? evidence.Objects.Where(item => item.Id == id)
                : request.Equals("selection", StringComparison.OrdinalIgnoreCase) || request.Equals("this object", StringComparison.OrdinalIgnoreCase)
                    ? evidence.Objects.Where(item => item.Id == selected)
                    : evidence.Objects.Where(item => item.Name.Equals(request, StringComparison.OrdinalIgnoreCase))).Take(2).ToArray();
            if (candidates.Length != 1)
                return new(QuestionStatus.EvidenceUnavailable, Message: candidates.Length == 2
                    ? "More than one object has that name. Select one and ask about selection, or use its exact ID."
                    : "There is no matching object in the authorized revision.");

            var source = candidates[0];
            if (query.Proposal is { Kind: SceneProposalKind.SetState } &&
                ((query.Criterion == "door" && source.Kind != SceneSourceKind.Door) ||
                 (query.Criterion == "lamp" && source.Kind != SceneSourceKind.Lamp)))
                return new(QuestionStatus.UnsupportedQuestion, Message: "Use open or closed for a door, and on or off for a lamp.");
            if (query.Criterion == "control" && source.Kind != SceneSourceKind.Switch)
                return new(QuestionStatus.UnsupportedQuestion, Message: "Choose a switch to inspect its link.");
            var target = source.TargetId is { } linked ? evidence.Objects.Single(item => item.Id == linked) : null;
            _latest = new SceneAnswer(grant.Scope, source, target, sha, query.Proposal);
            return new(QuestionStatus.Answered, _latest);
        }
    }

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
            movement.Value.Split(',', StringSplitOptions.TrimEntries) is [var x, var y] &&
            double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var dx) && dx is >= 0 and <= 1 &&
            double.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out var dy) && dy is >= 0 and <= 1)
            return new(movement.Name, new(SceneProposalKind.Move, X: dx, Y: dy));
        if (Strip("Rename ") is { } rename && Split(rename, " to ") is { } newName &&
            newName.Value.Length <= 96 && !newName.Value.Any(char.IsControl))
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

    private static (string Name, string Value)? Split(string input, string separator)
    {
        var index = input.LastIndexOf(separator, StringComparison.OrdinalIgnoreCase);
        if (index <= 0 || (index + separator.Length) >= input.Length)
            return null;
        return (input[..index].Trim(), input[(index + separator.Length)..].Trim());
    }

    private sealed record ParsedQuestion(string Name, SceneProposal? Proposal = null, string? Criterion = null);
}
