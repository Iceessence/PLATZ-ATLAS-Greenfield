using PlatzAtlas.Atlas;
using PlatzAtlas.Core;

internal static class LightPreviewChecks
{
    public static void Run(string directory)
    {
        var path = Path.Combine(directory, "preview-museum.platz");
        var editor = ProjectSession.NewMuseum();
        editor.Save(path);
        var saved = File.ReadAllBytes(path);
        var baseline = editor.Current;
        var west = baseline.Objects.Single(item => item.Location == "West Gallery");
        var east = baseline.Objects.Single(item => item.Location == "East Gallery");
        var westSwitch = baseline.Switches.Single();
        var scope = new ProjectReadScope(baseline.ProjectId, baseline.SceneId, baseline.Revision);
        var evidence = SwitchEvidenceProfile.Publish(scope, [new SwitchSource(westSwitch.Id, westSwitch.Name, westSwitch.TargetId)],
            baseline.Objects.Reverse()
                .Select(item => new LanternSource(item.Id, item.AssetId, item.Name, item.Location, item.IsOn)).ToArray());

        var host = new ProjectQuestionHost();
        var grant = host.Allow(scope, () => evidence);
        var ask = new SwitchQuestion(SwitchEvidenceProfile.Version, grant, ProjectQuestionHost.SupportedQuestion);
        var answer = host.Ask(ask).Answer ?? throw new InvalidOperationException("Expected the west switch answer.");
        var stages = 0;
        var (status, draft) = host.StagePreview(grant, scope, answer, Stage);
        Require(status == PreviewStatus.Staged && draft is not null, "authorized preview is staged");
        var preview = draft ?? throw new InvalidOperationException("Missing staged light preview.");
        Require(preview.Before == west && preview.After == (west with { IsOn = true })
            && preview.Operation.SwitchId == westSwitch.Id && preview.Operation.ExpectedIsOn == false,
            "typed proposal follows stable switch and object IDs and pins the old light value");
        Require(ReferenceEquals(editor.Current, baseline) && editor.Current.Objects.Contains(east)
            && !editor.CanUndo && !editor.CanRedo && !editor.IsDirty, "draft leaves canonical state and history intact");
        editor.Save();
        Require(saved.SequenceEqual(File.ReadAllBytes(path)) && !File.Exists(path + ".previous"),
            "ordinary Save during preview retains the original bytes");

        var comparison = ProjectSession.Open(path);
        Require(comparison.Apply(preview.Operation)
            && comparison.Current.Objects.Single(item => item.Id == west.Id) == preview.After
            && comparison.Current.Objects.Contains(east), "preview matches the same operation executed on a separate session");
        ExpectEditRejection(() => editor.PreviewSavedLight(preview.Operation with { ObjectId = east.Id }),
            EditRejection.WrongTarget);
        var badValue = preview.Operation with { ExpectedIsOn = true };
        ExpectEditRejection(() => editor.PreviewSavedLight(badValue), EditRejection.StaleValue);
        ExpectEditRejection(() => editor.Apply(badValue), EditRejection.StaleValue);
        Console.WriteLine("PASS detached preview matches the typed edit, pins the old state and leaves disk and undo history unchanged");

        var otherScope = scope with { ProjectId = ProjectSession.NewMuseum().Current.ProjectId };
        Denied(host.StagePreview(grant, otherScope, answer, Stage), PreviewStatus.AccessDenied);
        Denied(host.StagePreview(grant, scope with { SceneId = Guid.NewGuid() }, answer, Stage), PreviewStatus.AccessDenied);
        Denied(host.StagePreview(grant, scope with { Revision = scope.Revision + 1 }, answer, Stage), PreviewStatus.StaleBase);
        Denied(host.StagePreview(grant, scope, answer with { Lantern = answer.Lantern with { Id = east.Id } }, Stage),
            PreviewStatus.EvidenceUnavailable);
        Require(stages == 1, "no staging after wrong scope, stale base or altered answer");

        host.Revoke();
        Denied(host.StagePreview(grant, scope, answer, Stage), PreviewStatus.AccessDenied);
        var newGrant = host.Allow(scope, () => evidence);
        Denied(host.StagePreview(newGrant, scope, answer, Stage), PreviewStatus.EvidenceUnavailable);
        Require(stages == 1, "revocation and regrant cannot stage with the old answer");
        var currentAnswer = host.Ask(ask with { Grant = newGrant }).Answer
            ?? throw new InvalidOperationException("Expected a fresh answer.");
        var duringStage = host.StagePreview(newGrant, scope, currentAnswer, proposal =>
        {
            var staged = Stage(proposal);
            host.Revoke();
            return staged;
        });
        Denied(duringStage, PreviewStatus.AccessDenied);
        Require(stages == 2 && ReferenceEquals(editor.Current, baseline), "mid-stage revocation discards the detached result");

        comparison.Save();
        ExpectEditRejection(() => editor.PreviewSavedLight(preview.Operation), EditRejection.StaleRevision);
        Require(ProjectSession.Open(path).Current.Revision == baseline.Revision + 1
            && ReferenceEquals(editor.Current, baseline), "external publication prevents a newly staged preview");
        Require(editor.Apply(preview.Operation), "manual edit remains available on the open revision");
        ExpectEditRejection(() => editor.PreviewSavedLight(preview.Operation), EditRejection.StaleRevision);
        var dirtyRevision = preview.Operation with { BaseRevision = editor.Current.Revision, ExpectedIsOn = true, IsOn = false };
        ExpectEditRejection(() => editor.PreviewSavedLight(dirtyRevision), EditRejection.StaleRevision);
        Console.WriteLine("PASS forged, revoked, foreign, dirty, and stale light previews are rejected");

        LightPreview Stage(SwitchLightProposal proposal)
        {
            stages++;
            return editor.PreviewSavedLight(new SetLight(proposal.Scope.ProjectId, proposal.Scope.Revision,
                proposal.ObjectId, proposal.IsOn, proposal.SwitchId) { ExpectedIsOn = proposal.ExpectedIsOn });
        }
    }

    private static void Denied((PreviewStatus Status, LightPreview? Draft) result, PreviewStatus expected) =>
        Require(result.Status == expected && result.Draft is null, $"expected {expected} without a draft, got {result.Status}");

    private static void ExpectEditRejection(Action action, EditRejection rejection)
    {
        try
        {
            action();
        }
        catch (ProjectEditException error) when (error.Reason == rejection)
        {
            return;
        }
        throw new InvalidOperationException($"Expected {rejection}.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
