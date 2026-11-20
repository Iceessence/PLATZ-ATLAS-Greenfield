using PlatzAtlas.Core;
using PlatzAtlas.Runtime;

internal static class PixelDraftChecks
{
    public static void Run(string directory)
    {
        const uint outline = 0xaed58fff, interior = 0x78b5ea80, mark = 0xdd847fff;
        var pixels = new PixelDraft(16);
        pixels.BeginStroke();
        pixels.PaintLine(2, 2, 13, 2, outline);
        pixels.PaintLine(13, 2, 13, 13, outline);
        pixels.PaintLine(13, 13, 2, 13, outline);
        pixels.PaintLine(2, 13, 2, 2, outline);
        pixels.EndStroke();
        Require(pixels.Read(7, 2) == outline && pixels.Undo() && !pixels.CanUndo && pixels.Read(2, 2) == 0,
            "one undo reverses every segment of the stroke");
        Require(pixels.Redo() && pixels.Read(8, 13) == outline, "redo restores the stroke");
        pixels.Fill(4, 4, interior);
        Require(pixels.Read(4, 4) == interior && pixels.Read(1, 1) == 0 && pixels.Read(2, 2) == outline,
            "fill respects transparent exterior and RGBA boundary");
        pixels.BeginStroke();
        pixels.PaintLine(5, 5, 9, 9, 0);
        pixels.EndStroke();
        Require(pixels.Read(7, 7) == 0 && pixels.Undo() && pixels.Read(7, 7) == interior, "erase uses RGBA transparency");
        pixels.Resize(32);
        Require(pixels.Size == 32 && pixels.Read(8, 8) == interior && pixels.Undo() && pixels.Size == 16,
            "resizing uses nearest pixels and is reversible");
        var original = Asset("Signal crest", pixels);
        var detached = new PixelDraft(16, PixelAsset.Read(original));
        detached.Fill(4, 4, mark);
        var copy = Asset("Signal crest copy", detached);
        Require(copy.Id != original.Id && !copy.ContentBase64.Equals(original.ContentBase64, StringComparison.Ordinal),
            "editing a copy creates a new stable asset");
        Require(new PixelDraft(16, PixelAsset.Read(original)).Read(4, 4) == interior, "original asset stays byte-exact");

        var project = ProjectSession.NewScene();
        var id = project.Current.ProjectId;
        project.Apply(new ImportSceneAsset(id, 1, original));
        project.Apply(new ImportSceneAsset(id, 2, copy));
        var sprite = new SceneEntity(Guid.NewGuid(), "Placed crest", new(.45, .43, .27, .26, 0), new SceneSprite(copy.Id));
        project.Apply(new AddSceneEntity(id, 3, sprite));
        var path = Path.Combine(directory, "authored-sprite.platz");
        project.Save(path);
        var room = RoomPackage.Decode(RoomPackage.Encode(RuntimeRoom.FromProject(ProjectSession.Open(path).ReadSavedSnapshot())));
        Require(room.Entities?.Single() == sprite && room.RasterAssets?.Single() == copy,
            "only the placed authored variant is exported with lossless pixels");
        Console.WriteLine("PASS authored RGBA artwork, batched strokes, fill, erase, resize, immutable clone and room export");
    }

    private static ProjectAsset Asset(string name, PixelDraft draft) =>
        PixelAsset.Create(Guid.NewGuid(), name, draft.Size, draft.Size, draft.CopyRgba());

    private static void Require(bool result, string message)
    {
        if (!result) throw new InvalidOperationException(message);
    }
}
