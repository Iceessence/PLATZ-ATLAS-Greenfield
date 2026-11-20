using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlatzAtlas.Atlas;
using PlatzAtlas.Core;

internal static class AtlasReadChecks
{
    public static void Run(string directory)
    {
        var session = ProjectSession.NewMuseum();
        var savedPath = Path.Combine(directory, "atlas-museum.platz");
        session.Save(savedPath);
        var savedBytes = File.ReadAllBytes(savedPath);
        var museum = ProjectSession.Open(savedPath).Current;
        var scope = new ProjectReadScope(museum.ProjectId, museum.SceneId, museum.Revision);
        var west = museum.Objects.Single(item => item.Location == "West Gallery");
        var east = museum.Objects.Single(item => item.Location == "East Gallery");
        var sources = museum.Switches.Select(item => new SwitchSource(item.Id, item.Name, item.TargetId)).ToArray();
        var lanterns = museum.Objects.Reverse()
            .Select(item => new LanternSource(item.Id, item.AssetId, item.Name, item.Location, item.IsOn)).ToArray();
        var bytes = SwitchEvidenceProfile.Publish(scope, sources, lanterns);
        var snapshotPath = Path.Combine(directory, "atlas-switch-evidence.json");
        File.WriteAllBytes(snapshotPath, bytes);

        var host = new ProjectQuestionHost();
        var reads = 0;
        var grant = host.Allow(scope, () =>
        {
            reads++;
            return File.ReadAllBytes(snapshotPath);
        });
        var question = new SwitchQuestion(SwitchEvidenceProfile.Version, grant, ProjectQuestionHost.SupportedQuestion);
        Require(reads == 0, "allowing access must not read the source");
        var result = host.Ask(question);
        var answer = result.Answer ?? throw new InvalidOperationException("Expected a grounded switch answer.");
        Require(result.Status == QuestionStatus.Answered && reads == 1 && answer.Scope == scope,
            "authorized read uses the saved scoped snapshot once");
        Require(answer.Switch.Id == sources[0].Id && answer.Switch.TargetId == west.Id && answer.Lantern.Id == west.Id
            && answer.Lantern.AssetId == west.AssetId && answer.Lantern.Location == "West Gallery" && !answer.Lantern.IsOn,
            "follows stable reference, independent of same-name object order");
        Require(answer.Switch.TargetField == $"switches[{sources[0].Id:D}].targetId"
            && answer.Lantern.LocationField == $"objects[{west.Id:D}].location"
            && answer.Lantern.LightField == $"objects[{west.Id:D}].isOn", "exact source fields");
        using (var document = JsonDocument.Parse(bytes))
        {
            var json = document.RootElement.GetProperty("snapshot").GetRawText();
            Require(answer.SnapshotSha256 == Digest(json)
                && !json.Contains("contentBase64", StringComparison.Ordinal)
                && !json.Contains("history", StringComparison.Ordinal), "checksum and bounded source projection");
        }
        var uppercase = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes)
            .Replace(answer.SnapshotSha256, answer.SnapshotSha256.ToUpperInvariant(), StringComparison.Ordinal));
        var verifier = new ProjectQuestionHost();
        var uppercaseGrant = verifier.Allow(scope, () => uppercase);
        Require(verifier.Ask(question with { Grant = uppercaseGrant }).Status == QuestionStatus.Answered,
            "uppercase envelope checksum is accepted");
        Require(savedBytes.SequenceEqual(File.ReadAllBytes(savedPath)), "read did not modify the editable project");
        Console.WriteLine("PASS saved snapshot, stable switch target, exact evidence and read-only artifact");

        var anotherMuseum = ProjectSession.NewMuseum().Current;
        Require(anotherMuseum.Objects.Any(item => item.Id == west.Id), "foreign fixture reuses object IDs");
        Denied(host.Ask(question with
        {
            Grant = grant with { Scope = scope with { ProjectId = anotherMuseum.ProjectId } }
        }), QuestionStatus.AccessDenied);
        Denied(host.Ask(question with
        {
            Grant = grant with { Scope = scope with { SceneId = Guid.NewGuid() } }
        }), QuestionStatus.AccessDenied);
        Denied(host.Ask(question with
        {
            Grant = grant with { Scope = scope with { Revision = scope.Revision + 1 } }
        }), QuestionStatus.AccessDenied);
        Denied(host.Ask(question with { ProfileVersion = SwitchEvidenceProfile.Version + 1 }), QuestionStatus.IncompatibleProfile);
        Denied(host.Ask(question with { Text = "Turn on the west lantern." }), QuestionStatus.UnsupportedQuestion);
        host.Revoke();
        Denied(host.Ask(question), QuestionStatus.AccessDenied);
        Require(reads == 1, "wrong project, scene, revision, profile, unsupported question and revoked grant cause zero additional reads");
        var replacement = host.Allow(scope, () =>
        {
            reads++;
            return bytes;
        });
        Denied(host.Ask(question), QuestionStatus.AccessDenied);
        Require(reads == 1 && replacement.Id != grant.Id, "re-allowing the scope cannot revive the old reference");
        Console.WriteLine("PASS revoked and out-of-scope reads denied before touching the source");

        var rewired = SwitchEvidenceProfile.Publish(scope, [sources[0] with { TargetId = east.Id }], lanterns);
        var rewiredGrant = host.Allow(scope, () => rewired);
        var rewiredAnswer = host.Ask(question with { Grant = rewiredGrant }).Answer;
        Require(rewiredAnswer?.Lantern.Id == east.Id && rewiredAnswer.Lantern.IsOn,
            "reports the actual target when the wiring differs from the switch name");

        CheckDocument(host, scope, question, SwitchEvidenceProfile.Publish(scope,
            [sources[0], sources[0] with { Id = Guid.NewGuid() }], lanterns), QuestionStatus.EvidenceUnavailable);
        CheckDocument(host, scope, question, SwitchEvidenceProfile.Publish(scope,
            [sources[0] with { TargetId = Guid.NewGuid() }], lanterns), QuestionStatus.EvidenceUnavailable);
        CheckDocument(host, scope, question, SwitchEvidenceProfile.Publish(
            scope with { ProjectId = anotherMuseum.ProjectId }, sources, lanterns), QuestionStatus.AccessDenied);
        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes)
            .Replace("West Gallery", "Other Gallery", StringComparison.Ordinal));
        CheckDocument(host, scope, question, tampered, QuestionStatus.InvalidSnapshot);
        using (var document = JsonDocument.Parse(bytes))
        {
            var json = document.RootElement.GetProperty("snapshot").GetRawText();
            var incompatible = json.Replace("\"profileVersion\":1", "\"profileVersion\":2", StringComparison.Ordinal);
            var body = Encoding.UTF8.GetBytes($"{{\"sha256\":\"{Digest(incompatible)}\",\"snapshot\":{incompatible}}}");
            CheckDocument(host, scope, question, body, QuestionStatus.IncompatibleProfile);
        }
        var cancelledGrant = host.Allow(scope, () =>
        {
            host.Revoke();
            return bytes;
        });
        Denied(host.Ask(question with { Grant = cancelledGrant }), QuestionStatus.AccessDenied);
        Console.WriteLine("PASS rewired target, ambiguous evidence, corrupt and incompatible snapshots, and cancellation before delivery");
    }

    private static void CheckDocument(ProjectQuestionHost host, ProjectReadScope scope,
        SwitchQuestion question, byte[] bytes, QuestionStatus status)
    {
        var grant = host.Allow(scope, () => bytes);
        Denied(host.Ask(question with { Grant = grant }), status);
    }

    private static string Digest(string json) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));

    private static void Denied(SwitchQuestionResult result, QuestionStatus expected) =>
        Require(result.Status == expected && result.Answer is null, $"expected {expected} without evidence, got {result.Status}");

    private static void Require(bool actual, string check)
    {
        if (!actual)
            throw new InvalidOperationException(check);
    }
}
