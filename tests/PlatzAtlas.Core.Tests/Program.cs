using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlatzAtlas.Core;

var scratch = Directory.CreateTempSubdirectory("platz-atlas-core-");
try
{
    var room = ProjectSession.NewMuseum();
    var original = room.Current;
    var west = original.Objects.Single(item => item.Location == "West Gallery");
    var east = original.Objects.Single(item => item.Location == "East Gallery");
    var westSwitch = original.Switches.Single();
    Check(original.ProjectId != ProjectSession.NewMuseum().Current.ProjectId, "fresh project identities");
    Check(west.Id != east.Id && west.Name == east.Name && west.AssetId == east.AssetId, "same name, separate objects");
    Check(!west.IsOn && east.IsOn && westSwitch.TargetId == west.Id, "public fixture states and switch wiring");
    Check(!room.Undo() && !room.Redo(), "empty history");

    var request = new SetLight(original.ProjectId, original.Revision, west.Id, true, westSwitch.Id);
    ExpectEditRejection(room, request with { ObjectId = east.Id }, EditRejection.WrongTarget);
    ExpectEditRejection(room, request with { ProjectId = Guid.NewGuid() }, EditRejection.WrongProject);
    ExpectEditRejection(room, request with { ObjectId = Guid.NewGuid(), SwitchId = null }, EditRejection.MissingObject);
    Check(!room.Apply(request with { IsOn = false }) && ReferenceEquals(original, room.Current), "no-op and rejected edits");
    Console.WriteLine("PASS fixture, project identity, switch target and no-op");

    Check(room.Apply(request), "turn on west by stable identity");
    Check(room.Current.Revision == 2 && Light(room, west.Id).IsOn && Light(room, east.Id) == east, "revision 2");
    Check(!west.IsOn, "original snapshot immutability");
    ExpectEditRejection(room, request with { IsOn = false }, EditRejection.StaleRevision);
    Check(room.Current.Revision == 2 && Light(room, west.Id).IsOn && Light(room, east.Id) == east, "stale edit isolation");
    Check(room.Undo() && room.Current.Revision == 3 && Light(room, west.Id) == west, "undo at revision 3");
    Check(room.Redo() && room.Current.Revision == 4 && Light(room, west.Id).IsOn && Light(room, east.Id) == east, "redo at revision 4");
    Console.WriteLine("PASS stale revision rejection, unchanged east lantern, undo and redo");

    var path = Path.Combine(scratch.FullName, "museum.platz");
    room.Save(path);
    var saved = File.ReadAllBytes(path);
    var reopened = ProjectSession.Open(path);
    Check(!room.IsDirty && room.SavedRevision == 4 && reopened.SavedRevision == 4, "new save and open");
    Check(reopened.Current.ProjectId == original.ProjectId && reopened.Current.SceneId == original.SceneId, "stable project and scene");
    Check(Light(reopened, west.Id).IsOn && Light(reopened, east.Id) == east, "persisted lanterns");
    Check(reopened.Current.History.Select(edit => edit.ReversesRevision).SequenceEqual(new long?[] { null, 2, 3 }),
        "persistent edit, undo and redo lineage");
    Check(reopened.Undo(), "persistent undo after reload");
    Check(reopened.Current.Revision == 5 && Light(reopened, west.Id) == west, "reopened undo state");
    Console.WriteLine("PASS explicit save, reopened object identities and persistent edit history");

    // The read share permits validation but denies the delete access that Windows requires to replace the head.
    using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        ExpectSaveRejection(() => reopened.Save());
        Check(File.ReadAllBytes(path).SequenceEqual(saved), "failed replace kept last saved bytes");
        Check(reopened.IsDirty && reopened.SavedRevision == 4 && reopened.Current.Revision == 5,
            "failed replace kept pending edit and saved baseline");
        Check(ProjectSession.Open(path).Current.Revision == 4, "prior good file opens after failed replace");
        Check(!Directory.EnumerateFiles(scratch.FullName, "*.tmp").Any(), "failed replace staging cleanup");
    }

    reopened.Save();
    Check(!reopened.IsDirty && ProjectSession.Open(path).Current.Revision == 5, "retry publishes head");
    Check(File.ReadAllBytes(path + ".previous").SequenceEqual(saved), "previous file contains exact prior head");
    Check(Light(ProjectSession.Open(path), west.Id) == west, "retried edit is durable");
    room.Apply(new SetLight(original.ProjectId, 4, east.Id, false));
    ExpectSaveRejection(() => room.Save());
    Check(room.IsDirty && room.SavedRevision == 4 && ProjectSession.Open(path).Current.Revision == 5,
        "stale editor cannot overwrite newer disk head");
    Console.WriteLine("PASS Windows sharing failure, retry, prior-byte preservation and stale disk rejection");

    var corrupted = Encoding.UTF8.GetBytes("{\"project\":");
    File.WriteAllBytes(path, corrupted);
    ExpectInvalid(() => ProjectSession.Open(path));
    Check(File.ReadAllBytes(path).SequenceEqual(corrupted), "bad head left intact for inspection");
    var recovered = ProjectSession.OpenPrevious(path);
    Check(recovered.FilePath is null && recovered.SavedRevision is null && recovered.Current.Revision == 4,
        "recovery loads previous revision as a copy");
    ExpectSaveRejection(() => recovered.Save(path, overwriteExisting: true));
    Check(File.ReadAllBytes(path + ".previous").SequenceEqual(saved), "damaged-head overwrite kept recovery bytes");
    var restoration = Directory.CreateDirectory(Path.Combine(scratch.FullName, "restored"));
    var restoredPath = Path.Combine(restoration.FullName, "museum.platz");
    recovered.Save(restoredPath);
    var restored = ProjectSession.Open(restoredPath);
    Check(restored.Current.Revision == 4 && Light(restored, west.Id).IsOn && Light(restored, east.Id) == east,
        "verified restore to an empty folder");
    Console.WriteLine("PASS corrupt-head rejection and independently reopened recovery copy");

    var altered = Path.Combine(scratch.FullName, "altered.platz");
    var source = Encoding.UTF8.GetString(saved);
    File.WriteAllText(altered, source.Replace("\"Night Museum\"", "\"Another Room\"", StringComparison.Ordinal));
    ExpectInvalid(() => ProjectSession.Open(altered));
    using var envelope = JsonDocument.Parse(saved);
    var payload = envelope.RootElement.GetProperty("project").GetRawText();
    WriteWithDigest(altered, payload.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal));
    ExpectInvalid(() => ProjectSession.Open(altered));
    WriteWithDigest(altered, payload.Replace("\"isOn\": true", "\"isOn\": null", StringComparison.Ordinal));
    ExpectInvalid(() => ProjectSession.Open(altered));
    Console.WriteLine("PASS checksum, version and typed-property validation");
}
finally
{
    var tempRoot = Path.GetFullPath(Path.GetTempPath());
    if (scratch.Parent is { } parent &&
        Path.Equals(Path.TrimEndingDirectorySeparator(parent.FullName), Path.TrimEndingDirectorySeparator(tempRoot)))
    {
        scratch.Delete(recursive: true);
    }
}

static Lantern Light(ProjectSession session, Guid id) => session.Current.Objects.Single(item => item.Id == id);

static void Check(bool condition, string label)
{
    if (!condition)
    {
        throw new InvalidOperationException($"FAIL {label}");
    }
}

static void ExpectEditRejection(ProjectSession session, SetLight request, EditRejection expected)
{
    try
    {
        session.Apply(request);
        throw new InvalidOperationException($"FAIL accepted {expected}");
    }
    catch (ProjectEditException exception) when (exception.Reason == expected)
    {
    }
}

static void ExpectSaveRejection(Action save)
{
    try
    {
        save();
        throw new InvalidOperationException("FAIL accepted unsafe save");
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
    }
}

static void ExpectInvalid(Func<ProjectSession> open)
{
    try
    {
        open();
        throw new InvalidOperationException("FAIL opened invalid project");
    }
    catch (InvalidDataException)
    {
    }
}

static void WriteWithDigest(string path, string payload)
{
    var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    File.WriteAllText(path, $"{{\"sha256\":\"{digest}\",\"project\":{payload}}}");
}
