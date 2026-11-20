using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlatzAtlas.Core;
using PlatzAtlas.Runtime;

internal static class SceneImageChecks
{
    public static void Run(string directory)
    {
        byte[] pixels = [240, 112, 38, 255, 45, 125, 215, 0, 35, 155, 180, 128, 205, 138, 90, 255];
        var project = ProjectSession.NewScene();
        var id = project.Current.ProjectId;
        var image = PixelAsset.Create(Guid.NewGuid(), "Test art", 2, 2, pixels);
        var unused = PixelAsset.Create(Guid.NewGuid(), "Unused art", 2, 2, pixels);
        var sprite = new SceneEntity(Guid.NewGuid(), "Trail sign", new(.43, .32, .22, .27, 18), new SceneSprite(image.Id));
        Reject(() => project.Apply(new AddSceneEntity(id, 1, sprite)));
        Reject(() => project.Apply(new ImportSceneAsset(id, 1, image with { ContentBase64 = "b3RoZXI=" })));
        project.Apply(new ImportSceneAsset(id, 1, image));
        Require(project.Undo() && project.Current.Scene!.ActiveAssetIds is null, "undo deactivates imported image");
        Require(project.Redo() && project.Current.Scene!.ActiveAssetIds!.Single() == image.Id, "redo restores imported image");
        project.Apply(new ImportSceneAsset(id, 4, unused));
        project.Apply(new AddSceneEntity(id, 5, sprite));
        var path = Path.Combine(directory, "saved-image-scene.platz");
        project.Save(path);
        var original = File.ReadAllBytes(path);
        var saved = ProjectSession.Open(path);
        Require(saved.Current.Assets.Count == 2 && saved.Current.Scene!.Entities.Single() == sprite &&
            PixelAsset.Read(saved.Current.Assets.Single(a => a.Id == image.Id)).Rgba.Span.SequenceEqual(pixels),
            "pixel bytes, placement and asset ID reopen exactly");
        var package = RoomPackage.Encode(RuntimeRoom.FromProject(saved.ReadSavedSnapshot()));
        var runtime = RoomPackage.Decode(package);
        var exported = runtime.RasterAssets?.Single() ?? throw new InvalidOperationException("Expected one raster in the export.");
        Require(runtime.Entities!.Single() == sprite && exported.Id == image.Id &&
            PixelAsset.Read(exported).Rgba.Span.SequenceEqual(pixels), "export includes only the referenced raster");
        Reject(() => RoomPackage.Encode(runtime with { RasterAssets = [] }));
        Reject(() => RoomPackage.Encode(runtime with { RasterAssets = [image with { Sha256 = new string('a', 64) }] }));
        using var json = JsonDocument.Parse(original);
        var request = json.RootElement.GetProperty("project").GetRawText().Replace(image.Sha256, new string('b', 64));
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request)));
        File.WriteAllText(path, $"{{\"sha256\":\"{digest}\",\"project\":{request}}}");
        Reject(() => ProjectSession.Open(path));
        Console.WriteLine("PASS imported image identity, pixel bytes, durable history, missing/corrupt asset rejection and runtime readback");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Expected invalid image data rejection.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
