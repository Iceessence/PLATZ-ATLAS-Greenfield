using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlatzAtlas.Atlas;
using PlatzAtlas.Core;

internal static class ApprovedCommitChecks
{
    public static void Run(string directory)
    {
        var path = Path.Combine(directory, "approved.platz");
        var editor = ProjectSession.NewMuseum();
        editor.Save(path);
        var baseline = editor.Current;
        var before = File.ReadAllBytes(path);
        var oldReader = ProjectSession.Open(path);
        var clock = new TestClock();
        var (host, grant, proposal) = Stage(editor, clock);
        var scope = grant.Scope;
        var calls = 0;
        var review = OpenReview();
        Require(editor.Current.SchemaVersion == MuseumProject.LegacyFormatVersion && editor.LatestAtlasCommit is null,
            "review alone leaves the schema and history intact");
        Denied(host.CommitConfirmed(grant, scope with { Revision = scope.Revision + 1 }, review, Publish), CommitStatus.StaleBase);
        Denied(host.CommitConfirmed(grant, scope, review with { }, Publish), CommitStatus.AccessDenied);
        host.CancelCommitReview(review);
        Denied(host.CommitConfirmed(grant, scope, review, Publish), CommitStatus.AccessDenied);
        review = OpenReview();
        clock.Advance(ProjectQuestionHost.ReviewLifetime);
        Denied(host.CommitConfirmed(grant, scope, review, Publish), CommitStatus.Expired);
        Require(calls == 0 && before.SequenceEqual(File.ReadAllBytes(path)), "invalid confirmation never reached the writer");

        review = OpenReview();
        var checks = 0;
        var expiredAtReplacement = host.CommitConfirmed(grant, scope, review, (intent, require) =>
            editor.CommitApprovedLight(intent.OperationId, Request(intent), () =>
            {
                if (++checks == 2)
                    clock.Advance(ProjectQuestionHost.ReviewLifetime);
                require();
            }));
        Denied(expiredAtReplacement, CommitStatus.Expired);
        Require(checks == 2 && ReferenceEquals(editor.Current, baseline)
            && before.SequenceEqual(File.ReadAllBytes(path)) && !File.Exists(path + ".previous")
            && !Directory.EnumerateFiles(directory, "*.tmp").Any(), "expiry after flush leaves the prior head and cleans staging");

        review = OpenReview();
        checks = 0;
        var revokedAtReplacement = host.CommitConfirmed(grant, scope, review, (intent, require) =>
            editor.CommitApprovedLight(intent.OperationId, Request(intent), () =>
            {
                if (++checks == 2)
                    host.Revoke();
                require();
            }));
        Denied(revokedAtReplacement, CommitStatus.AccessDenied);
        Require(checks == 2 && ReferenceEquals(editor.Current, baseline) && before.SequenceEqual(File.ReadAllBytes(path)),
            "revocation after flush leaves the prior head");
        Console.WriteLine("PASS cancelled, forged, stale, expired and revoked confirmations, including at publication");

        (host, grant, proposal) = Stage(editor, clock);
        review = OpenReview();
        using (var owner = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Expect<IOException>(() => host.CommitConfirmed(grant, scope, review, Publish));
            Require(ReferenceEquals(editor.Current, baseline) && before.SequenceEqual(File.ReadAllBytes(path))
                && !editor.IsDirty && !editor.CanUndo, "publication failure rolls back without changing in-memory undo");
        }

        var result = host.CommitConfirmed(grant, scope, review, Publish);
        var receipt = result.Receipt ?? throw new InvalidOperationException("Expected a saved receipt.");
        var committed = File.ReadAllBytes(path);
        Require(result.Status == CommitStatus.Committed && receipt.OperationId == proposal.OperationId
            && receipt.BaseRevision == baseline.Revision && receipt.Revision == baseline.Revision + 1
            && receipt.ObjectId == proposal.ObjectId && !receipt.WasOn && receipt.IsOn,
            "successful confirmed proposal produces the exact receipt");
        Require(editor.Current.SchemaVersion == MuseumProject.FormatVersion && !editor.IsDirty && editor.CanUndo
            && before.SequenceEqual(File.ReadAllBytes(path + ".previous"))
            && editor.Current.Objects.Single(item => item.Location == "East Gallery") ==
                baseline.Objects.Single(item => item.Location == "East Gallery"), "save publishes one revision and leaves east intact");
        Denied(host.CommitConfirmed(grant, scope, review, Publish), CommitStatus.AccessDenied);

        var fresh = ProjectSession.Open(path);
        var request = Request(proposal);
        Require(fresh.FindAtlasCommit(receipt.OperationId) == receipt && fresh.LatestAtlasCommit == receipt
            && fresh.CommitApprovedLight(proposal.OperationId, request, static () => { }) == receipt
            && oldReader.CommitApprovedLight(proposal.OperationId, request, static () => { }) == receipt
            && committed.SequenceEqual(File.ReadAllBytes(path)) && before.SequenceEqual(File.ReadAllBytes(path + ".previous")),
            "reopen and old-reader replay return the durable receipt without a second write");
        Expect<ProjectEditException>(() => fresh.CommitApprovedLight(proposal.OperationId, request with { IsOn = false }, static () => { }),
            error => error.Reason == EditRejection.OperationIdConflict);

        using (var json = JsonDocument.Parse(committed))
        {
            var payload = json.RootElement.GetProperty("project").GetRawText()
                .Replace(receipt.PayloadSha256, new string('0', 64), StringComparison.Ordinal);
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
            var tampered = Path.Combine(directory, "bad-receipt.platz");
            File.WriteAllText(tampered, $"{{\"sha256\":\"{hash}\",\"project\":{payload}}}");
            Expect<InvalidDataException>(() => ProjectSession.Open(tampered));
        }

        Require(fresh.Undo() && !fresh.Current.Objects.Single(item => item.Id == request.ObjectId).IsOn,
            "fresh session can undo the saved, approved change");
        fresh.Save();
        Require(ProjectSession.Open(path).FindAtlasCommit(receipt.OperationId) == receipt,
            "the original receipt remains available after saving the reversal");
        Console.WriteLine("PASS atomic receipt, real file-lock retry, idempotent replay, digest validation, and reopened undo");

        var otherPath = Path.Combine(directory, "competing.platz");
        var reader = ProjectSession.NewMuseum();
        reader.Save(otherPath);
        var pinned = reader.Current;
        var (otherHost, otherGrant, otherProposal) = Stage(reader, TimeProvider.System);
        var losing = otherHost.BeginCommitReview(otherGrant, otherGrant.Scope, otherProposal).Review!;
        var writer = ProjectSession.Open(otherPath);
        var east = writer.Current.Objects.Single(item => item.Location == "East Gallery");
        writer.Apply(new SetLight(writer.Current.ProjectId, writer.Current.Revision, east.Id, false));
        writer.Save();
        var competing = File.ReadAllBytes(otherPath);
        Expect<ProjectEditException>(() => otherHost.CommitConfirmed(otherGrant, otherGrant.Scope, losing, (intent, require) =>
            reader.CommitApprovedLight(intent.OperationId, Request(intent), require)),
            error => error.Reason == EditRejection.StaleRevision);
        Require(ReferenceEquals(reader.Current, pinned) && competing.SequenceEqual(File.ReadAllBytes(otherPath))
            && ProjectSession.Open(otherPath).LatestAtlasCommit is null, "a competing manual save wins without a phantom receipt");
        Console.WriteLine("PASS approved publication rejects a competing save without touching either state");

        LightCommitReview OpenReview() => host.BeginCommitReview(grant, scope, proposal).Review ??
            throw new InvalidOperationException("Expected a review for the current staged proposal.");

        LightCommitReceipt Publish(SwitchLightProposal intent, Action require)
        {
            calls++;
            return editor.CommitApprovedLight(intent.OperationId, Request(intent), require);
        }
    }

    private static (ProjectQuestionHost Host, ProjectReadGrant Grant, SwitchLightProposal Proposal) Stage(
        ProjectSession session, TimeProvider clock)
    {
        var current = session.Current;
        var host = new ProjectQuestionHost(clock);
        var scope = new ProjectReadScope(current.ProjectId, current.SceneId, current.Revision);
        var grant = host.Allow(scope, () => SwitchEvidenceProfile.Publish(scope,
            current.Switches.Select(item => new SwitchSource(item.Id, item.Name, item.TargetId)).ToArray(),
            current.Objects.Select(item => new LanternSource(item.Id, item.AssetId, item.Name, item.Location, item.IsOn)).ToArray()));
        var answer = host.Ask(new SwitchQuestion(SwitchEvidenceProfile.Version, grant, ProjectQuestionHost.SupportedQuestion)).Answer!;
        var draft = host.StagePreview(grant, scope, answer, proposal =>
        {
            session.PreviewSavedLight(Request(proposal));
            return proposal;
        }).Draft ?? throw new InvalidOperationException("Expected a staged proposal.");
        return (host, grant, draft);
    }

    private static SetLight Request(SwitchLightProposal proposal) =>
        new(proposal.Scope.ProjectId, proposal.Scope.Revision, proposal.ObjectId, proposal.IsOn, proposal.SwitchId)
        { ExpectedIsOn = proposal.ExpectedIsOn };

    private static void Denied((CommitStatus Status, LightCommitReceipt? Receipt) result, CommitStatus expected) =>
        Require(result == (expected, null), $"expected {expected} with no receipt, got {result.Status}");

    private static void Expect<T>(Action run, Func<T, bool>? matches = null) where T : Exception
    {
        try { run(); }
        catch (T error) when (matches is null || matches(error)) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Require(bool passed, string message)
    {
        if (!passed)
            throw new InvalidOperationException(message);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _today = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _today;
        public void Advance(TimeSpan amount) => _today += amount;
    }
}
