using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlatzAtlas.Core;
using PlatzAtlas.Runtime;

internal static class SceneChecks
{
    public static void Run(string directory)
    {
        var author = ProjectSession.NewScene();
        var id = author.Current.ProjectId;
        Require(author.Current.Scene?.Entities.Count == 0 && author.Current.Objects.Count == 0 &&
            !author.CanUndo && author.Current.SchemaVersion == 4, "blank scene starts independently of the museum");
        var wall = new SceneEntity(Guid.NewGuid(), "Screen", new(.25, .40, .3, .2, 25), new WallPanel(PanelColor.Teal));
        var lamp = new SceneEntity(Guid.NewGuid(), "Screen", new(.65, .70, .1, .15, 0), new SceneLamp(true));
        author.Apply(new AddSceneEntity(id, 1, wall));
        author.Apply(new AddSceneEntity(id, 2, lamp));
        author.Apply(new RenameSceneEntity(id, 3, lamp.Id, "Desk light"));
        var move = new TransformSceneEntity(id, 4, wall.Id, wall.Transform with { X = .52, Rotation = -31 });
        Require(author.Apply(move), "move wall by stable ID");
        Reject<ProjectEditException>(() => author.Apply(move));
        Reject<InvalidDataException>(() => author.Apply(move with
        {
            BaseRevision = 5, Transform = move.Transform with { X = double.NaN }
        }));
        Reject<ProjectEditException>(() => author.Apply(new SetSceneLamp(id, 5, wall.Id, false)));
        author.Apply(new DeleteSceneEntity(id, 5, lamp.Id));
        Reject<ProjectEditException>(() => author.Apply(new AddSceneEntity(id, 6, lamp)));
        Require(author.Undo() && author.Current.Scene!.Entities.Single(item => item.Id == lamp.Id).Name == "Desk light",
            "undo restores the same named object and stable ID");
        var projectPath = Path.Combine(directory, "blank-created.platz");
        author.Save(projectPath);
        var baseBytes = File.ReadAllBytes(projectPath);
        var other = ProjectSession.Open(projectPath);
        var fresh = ProjectSession.Open(projectPath);
        Require(fresh.Current.Scene!.Entities.SequenceEqual(author.Current.Scene!.Entities) &&
            fresh.Current.Scene.Entities.Single(item => item.Id == wall.Id).Transform == move.Transform,
            "all scene transforms and identities survive reopen");
        Require(fresh.Redo() && fresh.Current.Scene!.Entities.Count == 1, "redo survives reopening");
        fresh.Save();
        Require(baseBytes.SequenceEqual(File.ReadAllBytes(projectPath + ".previous")) &&
            ProjectSession.OpenPrevious(projectPath).Current.Scene!.Entities.Count == 2, "recover prior scene");
        other.Apply(new SetSceneLamp(id, other.Current.Revision, lamp.Id, false));
        Reject<IOException>(() => other.Save());
        var package = Path.Combine(directory, "blank-created.platzroom");
        var room = RuntimeRoom.FromProject(fresh.ReadSavedSnapshot());
        RoomPackage.Save(package, room);
        var loaded = RoomPackage.Open(package);
        Require(loaded.Entities!.SequenceEqual(room.Entities!) &&
            RoomPackage.Encode(loaded).SequenceEqual(File.ReadAllBytes(package)), "runtime readback contains exact scene objects");
        var roomBytes = File.ReadAllBytes(package);
        var roomJson = Encoding.UTF8.GetString(roomBytes);
        Require(!roomJson.Contains("history", StringComparison.Ordinal), "the room package only contains runtime data");
        Reject<InvalidDataException>(() => RoomPackage.Decode(Encoding.UTF8.GetBytes(roomJson.Replace("Screen", "Other"))));
        using var doc = JsonDocument.Parse(File.ReadAllBytes(projectPath));
        var payload = doc.RootElement.GetProperty("project").GetRawText().Replace("\"rotation\": -31", "\"rotation\": 999");
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        File.WriteAllText(projectPath, $"{{\"sha256\":\"{digest}\",\"project\":{payload}}}");
        Reject<InvalidDataException>(() => ProjectSession.Open(projectPath));
        Require(baseBytes.SequenceEqual(File.ReadAllBytes(projectPath + ".previous")), "malformed scene leaves recovery bytes intact");
        Console.WriteLine("PASS blank scene, typed object operations, durable undo/redo, recovery and room export readback");
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
