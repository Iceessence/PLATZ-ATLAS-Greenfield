using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlatzAtlas.Atlas;
using PlatzAtlas.Core;
using PlatzAtlas.Runtime;

internal static class SceneAtlasChecks
{
    public static void Run(string directory)
    {
        var project = ProjectSession.NewScene();
        var pid = project.Current.ProjectId;
        var door = new SceneEntity(Guid.NewGuid(), "Gate", new(.5, .6, .2, .1, 0), new SceneDoor(false));
        var lamp = new SceneEntity(Guid.NewGuid(), "Gate", new(.8, .4, .1, .1, 0), new SceneLamp(true));
        var control = new SceneEntity(Guid.NewGuid(), "Reader", new(.5, .75, .05, .05, 45), new SceneSwitch(door.Id, SwitchBehavior.Toggle));
        project.Apply(new AddSceneEntity(pid, 1, door));
        project.Apply(new AddSceneEntity(pid, 2, lamp));
        project.Apply(new AddSceneEntity(pid, 3, control));
        var path = Path.Combine(directory, "evidence-project.platz");
        project.Save(path);
        var original = File.ReadAllBytes(path);
        var scope = new ProjectReadScope(pid, project.Current.SceneId, project.Current.Revision);
        var profile = SceneEvidenceProfile.Publish(scope,
        [
            Source(door, SceneSourceKind.Door, false),
            Source(lamp, SceneSourceKind.Lamp, true),
            Source(control, SceneSourceKind.Switch, target: door.Id, behavior: "Toggle")
        ]);
        var foreign = SceneEvidenceProfile.Publish(scope with { ProjectId = Guid.NewGuid() },
            [Source(door, SceneSourceKind.Door, false)]);
        var clock = new ReviewClock();
        var host = new SceneQuestionHost(clock);
        var grant = host.Allow(scope, () => foreign);
        Require(host.Ask(grant, 2, door.Id, "Inspect selection").Status == QuestionStatus.AccessDenied, "foreign evidence scope");
        var reads = 0;
        grant = host.Allow(scope, () => { reads++; project.ReadSavedSnapshot(); return profile; });
        var bad = host.Ask(grant, 1, null, "Inspect Gate");
        Require(bad.Status == QuestionStatus.IncompatibleProfile && reads == 0, "unknown profile reads nothing");
        var invalid = host.Ask(grant, 2, null, "delete the room");
        Require(invalid.Status == QuestionStatus.UnsupportedQuestion && reads == 0, "unsupported operation reads nothing");
        foreach (var blankRename in new[] { "Rename selection to    ", "Rename selection to \t ?", "Rename     to title" })
            Require(host.Ask(grant, 2, door.Id, blankRename).Status == QuestionStatus.UnsupportedQuestion && reads == 0,
                "incomplete rename never requests evidence or creates a proposal");
        Require(host.Ask(grant, 2, null, "Inspect Gate").Status == QuestionStatus.EvidenceUnavailable, "ambiguous name denied");
        var answer = host.Ask(grant, 2, lamp.Id, "Inspect selection").Answer!;
        Require(answer.Source is { Active: true } selected && selected.Id == lamp.Id && answer.Scope == scope, "selected lamp by stable identity");
        answer = host.Ask(grant, 2, null, "What does Reader control?").Answer!;
        Require(answer.Source?.Id == control.Id && answer.Target?.Id == door.Id && answer.Sha256.Length == 64,
            "resolved switch target with evidence digest");
        Reject<InvalidDataException>(() => SceneEvidenceProfile.Open(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(profile)
            .Replace("Reader", "Editor"))));

        answer = host.Ask(grant, 2, null, $"Move {door.Id:D} to 0.6, 0.55").Answer!;
        Require(answer.Proposal is { Kind: SceneProposalKind.Move, X: .6, Y: .55 }, "typed move proposal");
        var move = new TransformSceneEntity(pid, scope.Revision, door.Id, door.Transform with { X = .6, Y = .55 });
        var preview = project.PreviewSavedScene(move);
        Require(project.Current.Scene!.Entities.Contains(door) && original.SequenceEqual(File.ReadAllBytes(path)), "preview is detached");
        Reject<InvalidOperationException>(() => host.BeginReview(grant, answer with { }, scope));
        var review = host.BeginReview(grant, answer, scope);
        var completion = new Func<Guid, Action, SceneCommitReceipt>((id, check) => project.CommitApprovedScene(id, preview, check));
        host.DiscardReview();
        Reject<InvalidOperationException>(() => host.Confirm(review, scope, completion));
        review = host.BeginReview(grant, answer, scope);
        clock.Timestamp = TimeSpan.FromMinutes(1).Ticks;
        Reject<InvalidOperationException>(() => host.Confirm(review, scope, completion));
        review = host.BeginReview(grant, answer, scope);
        Reject<InvalidOperationException>(() => host.Confirm(review, scope, (id, check) =>
            project.CommitApprovedScene(id, preview, () =>
            {
                // Revoke at the publication callback after the staging write.
                if (++clock.ApprovalChecks == 2) host.Revoke();
                check();
            })));
        Require(original.SequenceEqual(File.ReadAllBytes(path)) && project.LatestSceneCommit is null, "revocation blocked publication");

        grant = host.Allow(scope, () => profile);
        answer = host.Ask(grant, 2, null, $"Move {door.Id:D} to 0.6, 0.55").Answer!;
        review = host.BeginReview(grant, answer, scope);
        var receipt = host.Confirm(review, scope, completion);
        var saved = ProjectSession.Open(path);
        Require(saved.LatestSceneCommit == receipt && saved.Current.Scene!.Entities.Single(item => item.Id == door.Id).Transform == move.Transform &&
            original.SequenceEqual(File.ReadAllBytes(path + ".previous")), "confirmed stable target and atomic proof survive reopen");
        var package = RoomPackage.Decode(RoomPackage.Encode(RuntimeRoom.FromProject(saved.ReadSavedSnapshot())));
        Require(package.Entities!.Single(item => item.Id == door.Id).Transform == move.Transform, "room export reflects approved transform");
        Require(saved.Undo() && saved.Current.Scene!.Entities.Contains(door) && saved.LatestSceneCommit == receipt,
            "Undo reverses the change and retains receipt");
        Require(saved.Redo() && saved.Current.Scene!.Entities.Single(item => item.Id == door.Id).Transform == move.Transform,
            "Redo restores the approved value");
        saved.Save();
        Require(ProjectSession.Open(path).LatestSceneCommit == receipt, "receipt survives reopening after Undo and Redo");
        Reject<ProjectEditException>(() => project.CommitApprovedScene(Guid.NewGuid(), preview, () => { }));
        using var json = JsonDocument.Parse(File.ReadAllBytes(path));
        var payload = json.RootElement.GetProperty("project").GetRawText().Replace(receipt.PayloadSha256, new string('a', 64));
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        File.WriteAllText(path, $"{{\"sha256\":\"{digest}\",\"project\":{payload}}}");
        Reject<InvalidDataException>(() => ProjectSession.Open(path));
        Require(ProjectSession.OpenPrevious(path).LatestSceneCommit == receipt, "prior valid receipt recoverable");
        Console.WriteLine("PASS scene ATLAS evidence, explicit approval, expiry, revocation, stale writes, durable receipts and export readback");
    }

    private static SceneSource Source(SceneEntity item, SceneSourceKind kind, bool? active = null, Guid? target = null, string? behavior = null)
    {
        var t = item.Transform;
        return new SceneSource(item.Id, item.Name, kind, t.X, t.Y, t.Width, t.Height, t.Rotation,
            active, target, behavior, null, null, null);
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class ReviewClock : TimeProvider
    {
        public long Timestamp { get; set; }
        public int ApprovalChecks { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Timestamp;
    }
}
