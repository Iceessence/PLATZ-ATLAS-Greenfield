using PlatzAtlas.Core;
using PlatzAtlas.Runtime;

internal static class AnimationChecks
{
    public static void Run(string scratch)
    {
        var room = ProjectSession.NewScene();
        var clipId = Guid.NewGuid();
        var first = new PixelDraft(256);
        first.BeginStroke();
        first.PaintLine(1, 1, 30, 30, 0x52c88480);
        first.EndStroke();
        var bgra = new byte[256 * 256 * 4];
        first.CopyBgraTo(bgra);
        Check(bgra.AsSpan((256 + 1) * 4, 4).SequenceEqual(new byte[] { 0x84, 0xc8, 0x52, 0x80 }), "large canvas preserves correct BGRA conversion");
        var second = new PixelDraft(256);
        var frames = new[]
        {
            new PixelFrame(100, new PixelAsset(256, 256, first.CopyRgba())),
            new PixelFrame(160, new PixelAsset(256, 256, second.CopyRgba()))
        };
        var asset = PixelClip.Create(clipId, "Pulse", frames);
        var clip = PixelClip.Read(asset);
        Check(clip.FrameAt(99) == 0 && clip.FrameAt(100) == 1 && clip.FrameAt(259) == 1 && clip.FrameAt(260) == 0,
            "per-frame durations and wrap remain deterministic");
        ExpectRejection(() => PixelClip.Read(asset with { Sha256 = new string('0', 64) }));
        ExpectRejection(() => PixelClip.Create(Guid.NewGuid(), "Too large", [frames[0], frames[0], frames[0], frames[0], frames[0]]));

        room.Apply(new ImportSceneAsset(room.Current.ProjectId, room.Current.Revision, asset));
        var sprite = new SceneEntity(Guid.NewGuid(), "Pulse", new Transform2D(.5, .5, .2, .2, 0), new SceneSprite(clipId));
        room.Apply(new AddSceneEntity(room.Current.ProjectId, room.Current.Revision, sprite));
        var path = Path.Combine(scratch, "animated.platz");
        room.Save(path);
        var opened = ProjectSession.Open(path);
        var packaged = RoomPackage.Decode(RoomPackage.Encode(RuntimeRoom.FromProject(opened.ReadSavedSnapshot())));
        var readback = PixelClip.Read(packaged.RasterAssets!.Single());
        Check(readback.Frames.Count == 2 && readback.Frames[0].Image.Rgba.Span.SequenceEqual(frames[0].Image.Rgba.Span) &&
            readback.Frames[1].DurationMs == 160 && packaged.Entities!.Single().Id == sprite.Id,
            "save and portable export preserve both pixel arrays, asset link and timing");
        Console.WriteLine("PASS large canvas, animated asset checksum and bounds, exact loop timing, save and package readback");
    }

    private static void ExpectRejection(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Expected animation data rejection.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
