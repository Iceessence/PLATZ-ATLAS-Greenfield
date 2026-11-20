using PlatzAtlas.Atlas;
using PlatzAtlas.Core;

internal static class WorkingPlanChecks
{
    public static void Run(string scratch)
    {
        var room = ProjectSession.NewScene();
        var scope = new ProjectReadScope(room.Current.ProjectId, room.Current.SceneId, room.Current.Revision)
            { WorkspaceId = room.WorkspaceId };
        var host = new SceneQuestionHost();
        var reads = 0;
        var grant = host.Allow(scope, () =>
        {
            reads++;
            return SceneEvidenceProfile.Publish(scope, []);
        });
        const string request = "Build a keyed gate named Birch on the left";
        Check(host.Ask(grant, SceneEvidenceProfile.Version, null, request + "; then erase the project").Status == QuestionStatus.UnsupportedQuestion &&
            reads == 0, "mixed instructions stop before reading evidence");
        var answer = host.Ask(grant, SceneEvidenceProfile.Version, null, request).Answer!;
        Check(answer.Plan is { Name: "Birch", Lane: "left" } && reads == 1, "the bounded plan parsed against authorized evidence");
        var door = SceneObjectFactory.Create(Guid.NewGuid(), SceneObjectKind.Door, "Birch door", .32, .6);
        var lever = SceneObjectFactory.Create(Guid.NewGuid(), SceneObjectKind.Switch, "Birch switch", .32, .73);
        var operations = new SceneOperation[]
        {
            new AddSceneEntity(scope.ProjectId, scope.Revision, door),
            new AddSceneEntity(scope.ProjectId, scope.Revision + 1, lever),
            new ConfigureSceneSwitch(scope.ProjectId, scope.Revision + 2, lever.Id, door.Id, SwitchBehavior.TurnOn)
        };
        var preview = room.PreviewWorkingPlan(operations);
        ExpectFailure(() => room.PreviewWorkingPlan([operations[0], operations[2]]));
        Check(room.Current.Revision == scope.Revision && room.Current.Scene!.Entities.Count == 0, "failed staging publishes no partial room");
        var review = host.BeginReview(grant, answer, scope);
        ExpectFailure(() => host.Confirm(review, scope, (id, approval) =>
            room.ApplyApprovedWorkingPlan(id, preview with { Entities = [door] }, approval)));
        var receipt = host.Confirm(review, scope, (id, approval) => room.ApplyApprovedWorkingPlan(id, preview, approval));
        Check(receipt == room.LatestScenePlan && receipt.Changes == 3 && room.FilePath is null, "only approved plan reaches working memory");
        Check(room.Undo() && room.Current.Scene!.Entities.Count == 0 && room.Redo() &&
            room.Current.Scene!.Entities.Single(item => item.Id == lever.Id).Component is SceneSwitch { TargetId: var linked } &&
            linked == door.Id, "a single undo and redo restore the whole dependent group");
        var path = Path.Combine(scratch, "grouped-plan.platz");
        room.Save(path);
        var reopened = ProjectSession.Open(path);
        Check(reopened.LatestScenePlan == receipt && reopened.Undo() && reopened.Current.Scene!.Entities.Count == 0,
            "group receipt and undo survive reopen");
        reopened.Save();
        Check(ProjectSession.Open(path).Redo(), "group redo survives a second reopen");
        Console.WriteLine("PASS bounded plan request, detached failure, forged preview rejection and atomic grouped approval/undo/reopen");
    }

    private static void ExpectFailure(Action action)
    {
        try { action(); }
        catch (ProjectEditException) { return; }
        throw new InvalidOperationException("Expected plan rejection.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
