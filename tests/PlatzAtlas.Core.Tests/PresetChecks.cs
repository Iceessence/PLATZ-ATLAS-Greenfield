using PlatzAtlas.Atlas;
using PlatzAtlas.Core;
using PlatzAtlas.Runtime;

internal static class PresetChecks
{
    public static void Run(string scratch)
    {
        var room = ProjectSession.NewScene();
        var shutter = StarterPresets.Items.Single(item => item.Template.Component is SceneDoor);
        var first = shutter.Place(Guid.NewGuid(), shutter.Template.Transform, "Flood barrier", new SceneDoor(false));
        var variant = ScenePreset.Decode(ScenePreset.FromObject(first).Encode());
        Check(variant.ParentId == shutter.Id && variant.Title == first.Name, "variant round-trip retains the preset relationship");
        var second = variant.Place(Guid.NewGuid(), variant.Template.Transform, "Garden exit", new SceneDoor(true));
        Add(first);
        Add(second);
        Check(room.Apply(new DeleteSceneEntity(room.Current.ProjectId, room.Current.Revision, first.Id)), "delete first preset instance");
        var numbers = SceneNumbers.FromHistory(room.Current.Scene!);
        var firstNumber = numbers[first.Id];
        var secondNumber = numbers[second.Id];
        Check(room.Undo() && room.Redo() && room.Undo(), "object can leave and return to the room");
        var duplicate = Guid.NewGuid();
        Check(room.Apply(new DuplicateSceneEntity(room.Current.ProjectId, room.Current.Revision, duplicate, second.Id)), "duplicate preset instance");
        var path = Path.Combine(scratch, "preset-instances.platz");
        room.Save(path);
        var opened = ProjectSession.Open(path);
        numbers = SceneNumbers.FromHistory(opened.Current.Scene!);
        Check(numbers[first.Id] == firstNumber && numbers[second.Id] == secondNumber && numbers[duplicate] > secondNumber &&
            opened.Current.Scene!.Entities.Single(item => item.Id == duplicate).PresetId == variant.Id,
            "assigned numbers and lineage survive delete/undo, duplicate and reopen");
        var package = RoomPackage.Decode(RoomPackage.Encode(RuntimeRoom.FromProject(opened.Current)));
        Check(package.Numbers!.Single(item => item.Id == first.Id).Number == firstNumber &&
            package.Entities!.Single(item => item.Id == first.Id).PresetId == shutter.Id, "export persists authoring identities");
        var scope = new ProjectReadScope(opened.Current.ProjectId, opened.Current.SceneId, opened.Current.Revision);
        var source = new SceneSource(first.Id, first.Name, SceneSourceKind.Door, .5, .5, .2, .15, 0, false, null, null, null, null, null)
            { Number = firstNumber };
        var source2 = source with { Id = second.Id, Number = secondNumber };
        var host = new SceneQuestionHost();
        var grant = host.Allow(scope, () => SceneEvidenceProfile.Publish(scope, [source, source2]));
        Check(host.Ask(grant, SceneEvidenceProfile.Version, null, $"Inspect #{firstNumber:D6}").Answer?.Source?.Id == first.Id &&
            host.Ask(grant, SceneEvidenceProfile.Version, null, first.Name.Insert(0, "Inspect ")).Status == QuestionStatus.EvidenceUnavailable &&
            host.Ask(grant, SceneEvidenceProfile.Version, null, "Inspect #999999").Status == QuestionStatus.EvidenceUnavailable,
            "ATLAS resolves a readable ID exactly across ambiguous or absent sources");
        Console.WriteLine("PASS reusable presets, variant lineage, nonrecycled object numbers, exact ATLAS references and export readback");

        void Add(SceneEntity entity) => Check(room.Apply(new AddSceneEntity(room.Current.ProjectId, room.Current.Revision, entity)), "place named preset");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
