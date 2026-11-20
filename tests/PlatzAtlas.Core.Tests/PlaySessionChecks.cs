using PlatzAtlas.Core;
using PlatzAtlas.Runtime;

internal static class PlaySessionChecks
{
    public static void Run(string directory)
    {
        var path = Path.Combine(directory, "play.platz");
        var editor = ProjectSession.NewMuseum();
        RejectUnpinned(editor);
        editor.Save(path);

        var west = editor.Current.Objects.Single(item => item.Location == "West Gallery");
        var east = editor.Current.Objects.Single(item => item.Location == "East Gallery");
        var westSwitch = editor.Current.Switches.Single();
        var receipt = editor.CommitApprovedLight(Guid.NewGuid(), new SetLight(
            editor.Current.ProjectId, editor.Current.Revision, west.Id, true, westSwitch.Id)
        { ExpectedIsOn = false }, static () => { });
        Require(editor.Undo(), "the fixture includes an available redo");
        RejectUnpinned(editor);
        editor.Save();
        var originalBytes = File.ReadAllBytes(path);
        var originalPrevious = File.ReadAllBytes(path + ".previous");
        var saved = editor.ReadSavedSnapshot();
        var beforeUndo = editor.CanUndo;
        var beforeRedo = editor.CanRedo;
        var play = new MuseumPlaySession(saved);
        var start = play.Current;
        Require(start.ProjectId == saved.ProjectId && start.SceneId == saved.SceneId && start.SourceRevision == saved.Revision
            && start.Step == 0 && start.Objects.SequenceEqual(saved.Objects), "Play starts from the exact saved snapshot");
        Require(!play.TryUseSwitch(Guid.NewGuid(), westSwitch.Id, out _)
            && !play.TryUseSwitch(start.SessionId, Guid.NewGuid(), out _) && ReferenceEquals(start, play.Current),
            "unknown switch or foreign session leaves state unchanged");

        Require(play.TryUseSwitch(start.SessionId, westSwitch.Id, out var first) && first is
            { Step: 1, WasOn: false, IsOn: true } && first.TargetId == west.Id && first.SwitchId == westSwitch.Id,
            "first switch use follows the stable west target");
        var frameOne = play.Current;
        Require(play.TryUseSwitch(start.SessionId, westSwitch.Id, out var second) && second is
            { Step: 2, WasOn: true, IsOn: false } && second.TargetId == west.Id,
            "second use toggles west back");
        Require(!start.Objects.Single(item => item.Id == west.Id).IsOn
            && frameOne.Objects.Single(item => item.Id == west.Id).IsOn
            && play.Current.Objects.SequenceEqual(saved.Objects) && play.Current.Objects.Contains(east),
            "old frames stay pinned and east stays unchanged");

        Require(play.TryUseSwitch(start.SessionId, westSwitch.Id, out _), "make a visibly different state before stopping");
        play.Stop();
        Require(!play.TryUseSwitch(start.SessionId, westSwitch.Id, out _) && play.Current.Step == 3,
            "stopped sessions reject input");
        Require(ReferenceEquals(saved, editor.Current) && editor.CanUndo == beforeUndo && editor.CanRedo == beforeRedo
            && editor.LatestAtlasCommit == receipt && !editor.IsDirty && originalBytes.SequenceEqual(File.ReadAllBytes(path))
            && originalPrevious.SequenceEqual(File.ReadAllBytes(path + ".previous")), "Play leaves authoring, receipt, history and files intact");
        Console.WriteLine("PASS saved Play snapshot, stable switch toggles, immutable frames, and authoring isolation");

        var restarted = new MuseumPlaySession(editor.ReadSavedSnapshot());
        Require(restarted.Current.SessionId != start.SessionId && restarted.Current.Step == 0
            && !restarted.TryUseSwitch(start.SessionId, westSwitch.Id, out _), "restart rejects old input and reloads saved state");
        var empty = new MuseumPlaySession(saved with { Switches = [] });
        Require(empty.Switches.Count == 0 && !empty.TryUseSwitch(empty.Current.SessionId, westSwitch.Id, out _),
            "empty switch list stays playable with no actions");
        try
        {
            _ = new MuseumPlaySession(saved with { Switches = [westSwitch with { TargetId = Guid.NewGuid() }] });
            throw new InvalidOperationException("Expected an invalid target to fail at startup.");
        }
        catch (InvalidDataException) { }

        var other = ProjectSession.Open(path);
        Require(other.Apply(new SetLight(saved.ProjectId, saved.Revision, east.Id, false)), "another window can edit east");
        other.Save();
        RejectUnpinned(editor);
        Require(restarted.Current.Objects.Contains(east) && restarted.Current.SourceRevision == saved.Revision
            && ProjectSession.Open(path).Current.Revision == saved.Revision + 1,
            "already-pinned Play is isolated while new Play refuses the superseded head");
        Console.WriteLine("PASS Play restart, stale inputs, empty and invalid wiring, and changed disk head");
    }

    private static void RejectUnpinned(ProjectSession editor)
    {
        try { editor.ReadSavedSnapshot(); }
        catch (ProjectEditException error) when (error.Reason == EditRejection.StaleRevision) { return; }
        throw new InvalidOperationException("Expected an unavailable saved revision to be rejected.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
