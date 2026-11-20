using PlatzAtlas.Atlas;
using PlatzAtlas.Core;
using PlatzAtlas.Runtime;

internal static class WorkingDocumentChecks
{
    public static void Run(string scratch)
    {
        var room = ProjectSession.NewScene();
        var initial = room.FreezeWorkingSnapshot();
        var door = SceneObjectFactory.Create(Guid.NewGuid(), SceneObjectKind.Door, "Switchback shutter", .5, .6);
        var trigger = SceneObjectFactory.Create(Guid.NewGuid(), SceneObjectKind.Switch, "Switchback console", .5, .85)
            with { Component = new SceneSwitch(door.Id, SwitchBehavior.Toggle) };
        room.Apply(new AddSceneEntity(room.Current.ProjectId, room.Current.Revision, door));
        room.Apply(new AddSceneEntity(room.Current.ProjectId, room.Current.Revision, trigger));
        var snapshot = room.FreezeWorkingSnapshot();
        Check(snapshot.WorkspaceId == initial.WorkspaceId && snapshot.Project.Revision > initial.Project.Revision &&
            initial.Project.Scene!.Entities.Count == 0, "version advances without mutating earlier snapshots");

        var host = new SceneQuestionHost();
        var scope = new ProjectReadScope(room.Current.ProjectId, room.Current.SceneId, room.Current.Revision) { WorkspaceId = snapshot.WorkspaceId };
        var sources = new[] { new SceneSource(door.Id, door.Name, SceneSourceKind.Door, .5, .6, .2, .1, 0, false, null, null, null, null, null) };
        var crossed = host.Allow(scope, () => SceneEvidenceProfile.Publish(scope with { WorkspaceId = Guid.NewGuid() }, sources));
        Check(host.Ask(crossed, SceneEvidenceProfile.Version, null, "Inspect Switchback shutter").Status == QuestionStatus.AccessDenied,
            "another workspace cannot serve a grant at the same project revision");
        var grant = host.Allow(scope, () => SceneEvidenceProfile.Publish(scope, sources));
        var answer = host.Ask(grant, SceneEvidenceProfile.Version, null, "Rename Switchback shutter to Main exit").Answer!;
        var review = host.BeginReview(grant, answer, scope);
        var rename = new RenameSceneEntity(room.Current.ProjectId, room.Current.Revision, door.Id, "Main exit");
        var draft = room.PreviewWorkingScene(rename);
        var receipt = host.Confirm(review, scope, (id, approval) => room.ApplyApprovedWorkingScene(id, draft, approval));
        Check(room.FilePath is null && room.IsDirty && receipt == room.LatestSceneCommit &&
            snapshot.Project.Scene!.Entities.Contains(door), "authorized apply is reversible and stays in memory");
        Check(room.Undo() && room.Current.Scene!.Entities.Contains(door) && room.Redo(), "undo and redo create new working revisions");
        ExpectFailure(() => room.ApplyApprovedWorkingScene(review.Id, draft, () => { }));

        var play = new MuseumPlaySession(room.FreezeWorkingSnapshot().Project);
        Check(play.TryUseNearbySwitch(play.Current.SessionId, out var used) && used.IsOn, "unsaved switch operates in Play");
        Check(room.Current.Scene!.Entities.Single(item => item.Id == door.Id).Component is SceneDoor { IsOpen: false },
            "Play cannot modify the working project");

        var path = Path.Combine(scratch, "working-document.platz");
        room.Save(path);
        var opened = ProjectSession.Open(path);
        Check(opened.Current.ProjectId == room.Current.ProjectId && opened.WorkspaceId != room.WorkspaceId &&
            opened.LatestSceneCommit == receipt, "explicit save stores the edit and reopening creates a different workspace");
        var savedBytes = File.ReadAllBytes(path);
        var previousRevision = opened.Current.Revision;
        draft = opened.PreviewWorkingScene(rename with { BaseRevision = previousRevision, Name = "North exit" });
        var callbacks = 0;
        ExpectFailure(() => opened.ApplyApprovedWorkingScene(Guid.NewGuid(), draft, () =>
        {
            if (++callbacks == 2)
                throw new ProjectEditException(EditRejection.StaleRevision, "Approval was withdrawn.");
        }));
        Check(callbacks == 2 && opened.Current.Revision == previousRevision && savedBytes.SequenceEqual(File.ReadAllBytes(path)),
            "approval withdrawn at the publication boundary leaves memory and disk untouched");
        opened.ApplyApprovedWorkingScene(Guid.NewGuid(), draft, () => { });
        Check(opened.IsDirty && opened.SavedRevision == previousRevision && savedBytes.SequenceEqual(File.ReadAllBytes(path)),
            "opening an existing project still keeps approved working edits off disk");
        ExpectFailure(() => opened.ReadSavedSnapshot());
        Console.WriteLine("PASS frozen working revisions, exact scope, in-memory approval, unsaved play, explicit save and saved-source export guard");
    }

    private static void ExpectFailure(Action action)
    {
        try { action(); }
        catch (ProjectEditException) { return; }
        throw new InvalidOperationException("Expected a rejected working revision.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
