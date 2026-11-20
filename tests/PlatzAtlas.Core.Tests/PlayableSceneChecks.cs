using PlatzAtlas.Core;
using PlatzAtlas.Runtime;

internal static class PlayableSceneChecks
{
    public static void Run(string directory)
    {
        var project = ProjectSession.NewScene();
        var id = project.Current.ProjectId;
        var door = new SceneEntity(Guid.NewGuid(), "Gate", new(.50, .60, .20, .09, 0), new SceneDoor(false));
        var control = new SceneEntity(Guid.NewGuid(), "Badge reader", new(.50, .73, .05, .075, 45),
            new SceneSwitch(null, SwitchBehavior.Toggle));
        var lamp = new SceneEntity(Guid.NewGuid(), "Beacon", new(.68, .45, .08, .12, 0), new SceneLamp(false));
        project.Apply(new AddSceneEntity(id, 1, door));
        project.Apply(new AddSceneEntity(id, 2, control));
        project.Apply(new AddSceneEntity(id, 3, lamp));
        Reject<InvalidDataException>(() => project.Apply(new ConfigureSceneSwitch(id, 4, control.Id, control.Id, SwitchBehavior.Toggle)));
        project.Apply(new ConfigureSceneSwitch(id, 4, control.Id, lamp.Id, SwitchBehavior.TurnOn));
        project.Apply(new ConfigureSceneSwitch(id, 5, control.Id, door.Id, SwitchBehavior.Toggle));
        Reject<InvalidDataException>(() => project.Apply(new DeleteSceneEntity(id, 6, door.Id)));
        Require(project.Undo() && project.Current.Scene!.Entities.Single(item => item.Id == control.Id).Component
            is SceneSwitch { TargetId: var reverted } && reverted == lamp.Id, "Undo restores previous wire");
        Require(project.Redo(), "Redo restores door link");
        project.Apply(new RenameSceneEntity(id, 8, door.Id, "Observatory gate"));
        project.Apply(new TransformSceneEntity(id, 9, control.Id, control.Transform with { X = .54 }));
        var path = Path.Combine(directory, "gate-project.platz");
        project.Save(path);
        var bytes = File.ReadAllBytes(path);
        var saved = ProjectSession.Open(path).ReadSavedSnapshot();
        var room = RuntimeRoom.FromProject(saved);
        var playable = Path.Combine(directory, "gate-room.platzroom");
        RoomPackage.Save(playable, room);
        var session = new MuseumPlaySession(RoomPackage.Open(playable));
        var sessionId = session.Current.SessionId;
        Require(session.Markers.Single().Position == new RoomPosition(.54, .73) &&
            !session.TryUseSwitch(sessionId, control.Id, out _), "placed switch rejects out-of-range use");
        for (var i = 0; i < 5; i++)
            Require(session.TryWalk(sessionId, WalkDirection.Up), "step toward gate");
        Require(!session.TryWalk(sessionId, WalkDirection.Up), "closed door blocks movement");
        Require(session.TryUseNearbySwitch(sessionId, out var opened) && opened.IsDoor &&
            opened.TargetId == door.Id && opened.TargetLocation == "Observatory gate" && opened.IsOn, "uses saved stable target");
        Require(session.TryWalk(sessionId, WalkDirection.Up), "entry into open doorway");
        Require(session.TryUseNearbySwitch(sessionId, out var safe) && safe.IsOn,
            "closing keeps door clear of visitor");
        for (var i = 0; i < 4; i++)
            Require(session.TryWalk(sessionId, WalkDirection.Up), "traverse open gate");
        Require(session.Current.Visitor.Y < .54, "visitor reached the other side");
        Require(session.Current.Entities!.Single(item => item.Id == lamp.Id).Component is SceneLamp { IsOn: false },
            "only the linked target changes");
        session.Stop();
        Require(!session.TryWalk(sessionId, WalkDirection.Up) && !session.TryUseNearbySwitch(sessionId, out _),
            "stopped session cannot change state");
        var restarted = new MuseumPlaySession(RoomPackage.Open(playable));
        Require(restarted.Current.Entities!.Single(item => item.Id == door.Id).Component is SceneDoor { IsOpen: false } &&
            restarted.Current.SessionId != sessionId && bytes.SequenceEqual(File.ReadAllBytes(path)), "read-only play and restart");
        var blocked = room with { Entities = [new SceneEntity(Guid.NewGuid(), "Solid", new(.5, .5, 1, 1, 0), new WallPanel(PanelColor.Slate))] };
        Reject<InvalidDataException>(() => RoomPackage.Encode(blocked));
        var broken = room with { Entities = room.Entities!.Where(item => item.Id != door.Id).ToArray() };
        Reject<InvalidDataException>(() => RoomPackage.Encode(broken));
        Console.WriteLine("PASS positioned switches, edited links, door and wall collision, proximity and playable export isolation");
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
}
