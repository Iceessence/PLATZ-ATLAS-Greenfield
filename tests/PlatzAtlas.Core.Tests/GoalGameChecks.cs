using System.Text;
using PlatzAtlas.Core;
using PlatzAtlas.Runtime;

internal static class GoalGameChecks
{
    public static void Run(string directory)
    {
        var editor = ProjectSession.NewScene();
        var viewer = Add("Start pad", .50, .90, .06, .06, new SceneSpawn());
        var pickup = Add("Copper key", .50, .78, .055, .06, new SceneKey());
        var door = Add("Relay shutter", .50, .60, .21, .09, new SceneDoor(false));
        var lamp = Add("Signal beacon", .70, .24, .08, .12, new SceneLamp(false));
        var control = Add("Relay console", .50, .73, .04, .07, new SceneSwitch(null, SwitchBehavior.Toggle));
        var goal = Add("Launch bay", .50, .25, .14, .12, new SceneGoal(1));
        var id = editor.Current.ProjectId;
        editor.Apply(new ConfigureSceneSwitch(id, editor.Current.Revision, control.Id, door.Id, SwitchBehavior.TurnOn)
        {
            SecondTargetId = lamp.Id
        });
        Reject<InvalidDataException>(() => editor.Apply(new DeleteSceneEntity(id, editor.Current.Revision, lamp.Id)));
        Reject<InvalidDataException>(() => Add("Extra spawn", .12, .1, .05, .05, new SceneSpawn()));
        Require(editor.Current.SchemaVersion == 5 && editor.Undo() && editor.Redo(), "schema 5 edits have canonical undo/redo");
        var path = Path.Combine(directory, "key-game.platz");
        editor.Save(path);
        var bytes = File.ReadAllBytes(path);
        var saved = ProjectSession.Open(path).ReadSavedSnapshot();
        var room = RuntimeRoom.FromProject(saved);
        var packed = RoomPackage.Encode(room);
        var loaded = RoomPackage.Decode(packed);
        Require(loaded.Version == 3 && loaded.Profile == RoomPackage.GameProfile &&
            loaded.Entities!.SequenceEqual(room.Entities!) && RoomPackage.Encode(loaded).SequenceEqual(packed),
            "schema 5 and room v3 round trip stable identities and bytes");
        Reject<InvalidDataException>(() => RoomPackage.Decode(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(packed)
            .Replace("\"version\":3", "\"version\":2").Replace("room-game/3", "room-scene/2"))));
        Reject<InvalidDataException>(() => RoomPackage.Encode(room with
        {
            Entities = room.Entities!.Where(item => item.Id != pickup.Id).ToArray()
        }));
        Reject<InvalidDataException>(() => RoomPackage.Encode(room with
        {
            Entities = room.Entities!.Select(item => item.Id == viewer.Id ? item with
                { Transform = item.Transform with { Y = door.Transform.Y } } : item).ToArray()
        }));

        var test = new MuseumPlaySession(loaded);
        var atLockedGoal = new MuseumPlaySession(room with
        {
            Entities = room.Entities!.Select(item => item.Id == viewer.Id ? item with
                { Transform = item.Transform with { Y = goal.Transform.Y } } : item).ToArray()
        });
        Require(atLockedGoal.Current.Progress is { CompletedGoal: null } progress && progress.Collected.Count == 0,
            "touching the goal without its key does not win");
        var session = test.Current.SessionId;
        Require(test.Current.Visitor == new RoomPosition(.50, .90) && test.Current.Progress?.Collected.Count == 0,
            "authored spawn and initial inventory");
        for (var i = 0; i < 5; i++) Require(test.TryWalk(session, WalkDirection.Up), "walk toward shutter");
        Require(test.Current.Progress!.Collected.SequenceEqual(new[] { pickup.Id }) && !test.TryWalk(session, WalkDirection.Up),
            "one key pickup and closed-door collision");
        Require(test.TryUseNearbySwitch(session, out var result) && result.TargetId == door.Id && result.IsOn &&
            result.AdditionalTarget is { } second && second.Id == lamp.Id && second.IsOn, "both linked targets activate");
        for (var i = 0; i < 20 && test.Current.Progress!.CompletedGoal is null; i++)
            Require(test.TryWalk(session, WalkDirection.Up), "walk through shutter to goal");
        Require(test.Current.Progress!.CompletedGoal == goal.Name && !test.TryWalk(session, WalkDirection.Down) &&
            !test.TryUseSwitch(session, control.Id, out _), "holding the key completes the goal and ends movement");
        Require(bytes.SequenceEqual(File.ReadAllBytes(path)) &&
            new MuseumPlaySession(loaded).Current.Progress?.Collected.Count == 0, "restart clears game state without modifying source");
        var old = RuntimeRoom.FromProject(ProjectSession.NewScene().Current);
        Require(RoomPackage.Decode(RoomPackage.Encode(old)).Version == 2 &&
            RoomPackage.Decode(RoomPackage.Encode(RuntimeRoom.FromProject(ProjectSession.NewMuseum().Current))).Version == 1,
            "legacy v1 and v2 remain readable");
        Console.WriteLine("PASS authored spawn, key pickup, linked targets, win and restart with schema 5 / room v3 and v1/v2 compatibility");

        SceneEntity Add(string name, double x, double y, double width, double height, SceneComponent component)
        {
            var entity = new SceneEntity(Guid.NewGuid(), name, new(x, y, width, height, 0), component);
            editor.Apply(new AddSceneEntity(editor.Current.ProjectId, editor.Current.Revision, entity));
            return entity;
        }
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Require(bool result, string message)
    {
        if (!result) throw new InvalidOperationException(message);
    }
}
