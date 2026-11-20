using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlatzAtlas.Core;
using PlatzAtlas.Runtime;

internal static class RoomPackageChecks
{
    public static void Run(string directory)
    {
        var source = Path.Combine(directory, "export-source.platz");
        var package = Path.Combine(directory, "museum.platzroom");
        var editor = ProjectSession.NewMuseum();
        editor.Save(source);
        var initial = File.ReadAllBytes(source);
        var room = RuntimeRoom.FromProject(editor.ReadSavedSnapshot());
        var bytes = RoomPackage.Encode(room);
        RoomPackage.Save(package, room);
        var decoded = RoomPackage.Open(package);
        Require(bytes.SequenceEqual(RoomPackage.Encode(decoded)) && bytes.SequenceEqual(File.ReadAllBytes(package)),
            "same saved scene yields stable package bytes");
        using (var envelope = JsonDocument.Parse(bytes))
        {
            var payload = envelope.RootElement.GetProperty("room");
            var checksum = envelope.RootElement.GetProperty("sha256").GetString()!;
            Require(!payload.TryGetProperty("history", out _) && !payload.TryGetProperty("assets", out _)
                && decoded.ProjectId == room.ProjectId && decoded.SourceRevision == room.SourceRevision,
                "package pins revision and carries runtime-only data");
            var uppercase = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes)
                .Replace(checksum, checksum.ToUpperInvariant(), StringComparison.Ordinal));
            Require(RoomPackage.Decode(uppercase).SourceRevision == room.SourceRevision, "uppercase package checksum is accepted");
        }
        Reject<ArgumentException>(() => RoomPackage.Save(source, room, overwrite: true));
        Reject<IOException>(() => RoomPackage.Save(package, room));
        Reject<InvalidDataException>(() => RoomPackage.Decode(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes)
            .Replace("Night Museum", "Night Archive", StringComparison.Ordinal))));
        Reject<InvalidDataException>(() => RoomPackage.Decode(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes)
            .Replace("night-museum/1", "night-museum/2", StringComparison.Ordinal))));
        Reject<InvalidDataException>(() => RoomPackage.Decode(new byte[256 * 1024 + 1]));
        var invalid = room with { Switches = [room.Switches[0] with { TargetId = Guid.NewGuid() }] };
        var invalidPayload = JsonSerializer.SerializeToUtf8Bytes(invalid, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var digest = Convert.ToHexStringLower(SHA256.HashData(invalidPayload));
        Reject<InvalidDataException>(() => RoomPackage.Decode(Encoding.UTF8.GetBytes(
            $"{{\"version\":1,\"runtimeProfile\":\"night-museum/1\",\"sha256\":\"{digest}\",\"room\":{Encoding.UTF8.GetString(invalidPayload)}}}")));
        Require(initial.SequenceEqual(File.ReadAllBytes(source)), "export and rejection preserve authoring file");
        Console.WriteLine("PASS deterministic saved-room export, digest/profile/bounds/reference validation and source isolation");

        var play = new MuseumPlaySession(decoded);
        var sessionId = play.Current.SessionId;
        Require(!play.TryUseNearbySwitch(sessionId, out _) && play.Current.Step == 0, "out-of-range input cannot act");
        WalkToWest(play);
        var target = room.Switches.Single().TargetId;
        Require(play.TryUseNearbySwitch(sessionId, out var first) && first is { IsOn: true } && first.TargetId == target,
            "walking into range uses the exact saved target");
        Require(play.TryUseNearbySwitch(sessionId, out var second) && second is { WasOn: true, IsOn: false }
            && second.TargetId == target, "default switch toggles back");
        for (var index = 0; index < 30; index++)
            play.TryWalk(sessionId, WalkDirection.Left);
        Require(play.Current.Visitor.X == 0.12 && !play.TryWalk(sessionId, WalkDirection.Left),
            "visitor stays on the bounded floor");
        play.Stop();
        Require(!play.TryWalk(sessionId, WalkDirection.Right) && !play.TryUseNearbySwitch(sessionId, out _),
            "stopping invalidates walking and use");
        Console.WriteLine("PASS standalone movement, activation radius and stable switch identity");

        var switchId = room.Switches.Single().Id;
        var edit = new SetSwitchBehavior(room.ProjectId, room.SourceRevision, switchId, SwitchBehavior.TurnOn);
        Require(editor.Apply(edit) && editor.Current.SchemaVersion == 3 && editor.Current.Objects.SequenceEqual(room.Objects),
            "mode is a typed edit that leaves light values alone");
        Reject<ProjectEditException>(() => editor.Apply(edit));
        Require(editor.Undo() && editor.Current.Switches.Single().Behavior is null, "Undo restores Toggle");
        Require(editor.Redo() && editor.Current.Switches.Single().Behavior == SwitchBehavior.TurnOn, "Redo restores Turn on");
        editor.Save();
        var reopened = ProjectSession.Open(source);
        var published = RuntimeRoom.FromProject(reopened.ReadSavedSnapshot());
        Require(published.SourceRevision == room.SourceRevision + 3 && published.Switches[0].Behavior == SwitchBehavior.TurnOn
            && initial.SequenceEqual(File.ReadAllBytes(source + ".previous")), "reopened behavior and previous legacy head");
        RoomPackage.Save(package, published, overwrite: true);
        var newPlay = new MuseumPlaySession(RoomPackage.Open(package));
        WalkToWest(newPlay);
        Require(newPlay.TryUseNearbySwitch(newPlay.Current.SessionId, out var on) && on is { WasOn: false, IsOn: true }
            && newPlay.TryUseNearbySwitch(newPlay.Current.SessionId, out var stays) && stays is { WasOn: true, IsOn: true },
            "published Turn on keeps the lantern lit on repeated use");
        Require(reopened.Undo() && reopened.Current.Switches.Single().Behavior is null,
            "Undo still works after reopening a behavior edit");
        Console.WriteLine("PASS switch mode authoring, persistent undo/redo, and re-exported Turn on behavior");
    }

    private static void WalkToWest(MuseumPlaySession session)
    {
        for (var index = 0; index < 5; index++)
            session.TryWalk(session.Current.SessionId, WalkDirection.Left);
        for (var index = 0; index < 2; index++)
            session.TryWalk(session.Current.SessionId, WalkDirection.Up);
    }

    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Require(bool valid, string message)
    {
        if (!valid)
            throw new InvalidOperationException(message);
    }
}
