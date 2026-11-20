using PlatzAtlas.Atlas;
using PlatzAtlas.Core;
using PlatzAtlas.Runtime;

internal static class SceneActionChecks
{
    public static void Run(string directory)
    {
        var editor = ProjectSession.NewScene();
        var p = editor.Current.ProjectId;
        var control = Add(SceneObjectKind.Switch, "Console", .5, .7);
        var lamp = Add(SceneObjectKind.Lamp, "Lamp", .2, .4);
        var door = Add(SceneObjectKind.Door, "Gate", .5, .4);
        var spawn = Add(SceneObjectKind.Spawn, "Entrance", .5, .4);
        var wallA = Add(SceneObjectKind.Wall, "Partition", .2, .6);
        var wallB = Add(SceneObjectKind.Wall, "Partition", .8, .6);
        var file = Path.Combine(directory, "scene-actions.platz");
        editor.Save(file);
        var initial = File.ReadAllBytes(file);
        var scope = new ProjectReadScope(p, editor.Current.SceneId, editor.Current.Revision);
        var evidence = SceneEvidenceProfile.Publish(scope, [
            Source(control, SceneSourceKind.Switch) with { Behavior = "Toggle" },
            Source(lamp, SceneSourceKind.Lamp) with { Active = true },
            Source(door, SceneSourceKind.Door) with { Active = false },
            Source(spawn, SceneSourceKind.Spawn) with { SpawnBlocked = MuseumPlaySession.SpawnBlocked(editor.Current.Scene!.Entities, new(.5, .4)) },
            Source(wallA, SceneSourceKind.Wall) with { Color = "Slate" },
            Source(wallB, SceneSourceKind.Wall) with { Color = "Slate" }
        ]);
        var host = new SceneQuestionHost();
        var reads = 0;
        var grant = host.Allow(scope, () => { reads++; return evidence; });
        var denied = host.Ask(grant with { Id = Guid.NewGuid() }, 2, null, "Check scene");
        Require(denied.Status == QuestionStatus.AccessDenied && reads == 0, "diagnostics cannot read with a forged grant");
        var health = host.Ask(grant, 2, null, "Check scene").Health!;
        Require(health.Scope == scope && health.Sha256.Length == 64 && health.Issues.Count == 3 &&
            health.Issues.Any(i => i.Code == "blocked-spawn" && i.ObjectIds.SequenceEqual(new[] { spawn.Id })) &&
            health.Issues.Any(i => i.Code == "unlinked-switch" && i.ObjectIds.SequenceEqual(new[] { control.Id })),
            "health reports audited IDs and leaves authoring unchanged");
        Require(host.Ask(grant, 2, null, "Wire Console to Partition").Status == QuestionStatus.EvidenceUnavailable &&
            host.Ask(grant, 2, null, "Wire Console to Entrance").Status == QuestionStatus.UnsupportedQuestion,
            "ambiguous and incompatible targets are refused");
        var wire = host.Ask(grant, 2, null, $"Wire Console to {door.Id:D}").Answer!;
        Require(wire.ProposedTarget?.Id == door.Id && wire.Proposal?.TargetId == door.Id && wire.Source?.Id == control.Id,
            "both objects of a proposed link are grounded");
        Require(host.Ask(grant, 2, null, "Unlink Console").Answer?.Proposal?.Kind == SceneProposalKind.Unlink &&
            host.Ask(grant, 2, null, "Resize Gate to .2, .09").Answer?.Proposal is { Kind: SceneProposalKind.Resize, X: .2, Y: .09 } &&
            host.Ask(grant, 2, null, "Rotate Gate to 90").Answer?.Proposal is { Kind: SceneProposalKind.Rotate, X: 90 } &&
            host.Ask(grant, 2, lamp.Id, "Duplicate selection").Answer?.Source?.Id == lamp.Id,
            "unlink, resize, rotate and duplicate retain typed operations");
        Require(host.Ask(grant, 2, null, "Resize Gate to 0, .5").Status == QuestionStatus.UnsupportedQuestion,
            "invalid size is refused before preview");
        var add = host.Ask(grant, 2, null, "Add lamp named Desk beacon at .24, .18").Answer!;
        Require(add.Source is null && add.Proposal is { ObjectKind: SceneSourceKind.Lamp, Kind: SceneProposalKind.Add },
            "creation binds to the scene without fabricating an existing object");
        var creation = SceneObjectFactory.Create(Guid.NewGuid(), SceneObjectKind.Lamp, add.Proposal!.Name!, add.Proposal.X, add.Proposal.Y);
        var draft = editor.PreviewSavedScene(new AddSceneEntity(p, scope.Revision, creation));
        Require(draft.Before is null && initial.SequenceEqual(File.ReadAllBytes(file)), "creation preview is detached");
        var review = host.BeginReview(grant, add, scope);
        var receipt = host.Confirm(review, scope, (id, check) => editor.CommitApprovedScene(id, draft, check));
        var copy = ProjectSession.Open(file);
        Require(receipt.EntityId == creation.Id && copy.LatestSceneCommit == receipt &&
            copy.Current.Scene!.Entities.Contains(creation) && copy.Undo() && !copy.Current.Scene!.Entities.Contains(creation) &&
            copy.Redo() && copy.Current.Scene!.Entities.Contains(creation), "approved creation has a durable receipt and undo/redo");
        Console.WriteLine("PASS scoped scene health, grounded links, typed geometry, duplicate and detached approved creation");

        SceneEntity Add(SceneObjectKind kind, string name, double x, double y)
        {
            var entity = SceneObjectFactory.Create(Guid.NewGuid(), kind, name, x, y);
            editor.Apply(new AddSceneEntity(p, editor.Current.Revision, entity));
            return entity;
        }
    }

    private static SceneSource Source(SceneEntity entity, SceneSourceKind kind)
    {
        var t = entity.Transform;
        return new SceneSource(entity.Id, entity.Name, kind, t.X, t.Y, t.Width, t.Height, t.Rotation, null, null, null, null, null, null);
    }

    private static void Require(bool result, string message)
    {
        if (!result) throw new InvalidOperationException(message);
    }
}
