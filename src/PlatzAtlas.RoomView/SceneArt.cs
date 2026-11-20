using System.Runtime.CompilerServices;
using PlatzAtlas.Core;
using PlatzAtlas.Rendering;
using PlatzAtlas.Runtime;

namespace PlatzAtlas.RoomView;

public readonly record struct RoomViewTransform(double Zoom, double CenterX, double CenterY)
{
    public static RoomViewTransform Fit => new(1, .5, .5);
    public (double X, double Y) ToWorld(double x, double y) => ((x - .5) / Zoom + CenterX, (y - .5) / Zoom + CenterY);
    public RenderPoint ToViewport(double x, double y) => new((float)(.5 + (x - CenterX) * Zoom), (float)(.5 + (y - CenterY) * Zoom));

    public RoomViewTransform Around(double x, double y, double factor)
    {
        var point = ToWorld(x, y);
        var zoom = Math.Clamp(Zoom * factor, .65, 12);
        return new RoomViewTransform(zoom, Math.Clamp(point.X - (x - .5) / zoom, -.4, 1.4),
            Math.Clamp(point.Y - (y - .5) / zoom, -.4, 1.4));
    }
}

public static class SceneArt
{
    private static readonly RenderColor Background = Rgb(17, 25, 33);
    private static readonly RenderColor Grid = Rgb(32, 44, 53);
    private static readonly RenderColor Selection = Rgb(255, 214, 152);
    private static readonly ConditionalWeakTable<ProjectAsset, RasterTexture> Images = new();

    public static RasterTexture Image(ProjectAsset source) => Images.GetValue(source, asset =>
    {
        var pixels = PixelAsset.Read(asset);
        return new RasterTexture(pixels.Width, pixels.Height, pixels.Rgba);
    });

    public static (RenderPoint Resize, RenderPoint Rotate) EditHandles(Transform2D t) =>
        (Place(t, t.Width / 2, t.Height / 2), Place(t, 0, -t.Height / 2 - .045));

    public static RenderSnapshot Create(Guid projectId, long revision, IReadOnlyList<SceneEntity> entities,
        Guid? selected, bool grid = false, PlayRoomSnapshot? play = null, Guid? nearbySwitch = null,
        IReadOnlyList<ProjectAsset>? assets = null, uint? backgroundRgb = null, IReadOnlyList<Guid>? paintOrder = null,
        bool editHandles = false, RoomViewTransform? view = null, double gridSpacing = .05)
    {
        var projection = view ?? RoomViewTransform.Fit;
        var triangles = new List<RenderTriangle>();
        var foreground = new List<RenderTriangle>();
        var sprites = new List<RenderSprite>();
        var surface = triangles;
        var background = backgroundRgb is uint rgb ? Rgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb) : Background;
        var chart = backgroundRgb is not null ? new RenderColor(
            background.R * .85f + .10f, background.G * .85f + .10f, background.B * .85f + .10f) : Grid;
        if (grid)
        {
            var (left, top) = projection.ToWorld(0, 0);
            var (right, bottom) = projection.ToWorld(1, 1);
            Quad(new Transform2D(.5, .5, 1, 1, 0), background);
            var interval = Math.Clamp(gridSpacing, .025, .1);
            var major = new RenderColor(chart.R + .045f, chart.G + .05f, chart.B + .055f);
            for (var column = (int)Math.Ceiling(left / interval); column <= Math.Floor(right / interval); column++)
                Quad(new Transform2D(column * interval, projection.CenterY, .001 / projection.Zoom, bottom - top, 0), column % 4 == 0 ? major : chart);
            for (var row = (int)Math.Ceiling(top / interval); row <= Math.Floor(bottom / interval); row++)
                Quad(new Transform2D(projection.CenterX, row * interval, right - left, .0017 / projection.Zoom, 0), row % 4 == 0 ? major : chart);
        }

        foreach (var item in SceneAppearance.Ordered(entities, paintOrder))
        {
            if (play is not null && (item.Component is SceneSpawn || play.Progress?.Collected.Contains(item.Id) == true))
                continue;
            var t = item.Transform;
            if (item.Id == selected)
            {
                surface = foreground;
                AtLocal(t, -.508, 0, .025, 1.026, Selection);
                AtLocal(t, .508, 0, .025, 1.026, Selection);
                AtLocal(t, 0, -.508, 1.026, .025, Selection);
                AtLocal(t, 0, .508, 1.026, .025, Selection);
                if (editHandles)
                {
                    var (resize, rotate) = EditHandles(t);
                    Quad(new Transform2D(resize.X, resize.Y, .018, .03, 0), Selection);
                    Quad(new Transform2D(rotate.X, rotate.Y, .016, .026, 45), Rgb(126, 207, 230));
                }
                surface = triangles;
            }
            if (item.Component is WallPanel panel)
            {
                Quad(t, Rgb(39, 52, 64));
                Quad(t with { Width = t.Width * .90, Height = t.Height * .84 }, panel.Color switch
                {
                    PanelColor.Teal => Rgb(54, 121, 121),
                    PanelColor.Sand => Rgb(170, 125, 80),
                    PanelColor.Plum => Rgb(118, 81, 130),
                    _ => Rgb(83, 104, 126)
                });
            }
            else if (item.Component is SceneLamp light)
            {
                Quad(t, light.IsOn ? Rgb(122, 91, 52) : Rgb(51, 66, 68));
                Quad(t with { Width = t.Width * .74, Height = t.Height * .74 }, light.IsOn
                    ? Rgb(212, 143, 71) : Rgb(69, 87, 84));
                Quad(t with { Width = t.Width * .47, Height = t.Height * .47 }, light.IsOn
                    ? Rgb(255, 217, 128) : Rgb(108, 127, 111));
            }
            else if (item.Component is SceneDoor door)
            {
                if (door.IsOpen)
                {
                    AtLocal(t, -.43, 0, .14, 1, Rgb(79, 154, 132));
                    AtLocal(t, .43, 0, .14, 1, Rgb(79, 154, 132));
                }
                else
                {
                    Quad(t, Rgb(57, 44, 42));
                    Quad(t with { Width = t.Width * .9, Height = t.Height * .72 }, Rgb(174, 104, 77));
                    AtLocal(t, .29, 0, .10, .25, Rgb(248, 214, 150));
                }
            }
            else if (item.Component is SceneSwitch control)
            {
                Quad(t, Rgb(33, 47, 56));
                Quad(t with { Width = t.Width * .58, Height = t.Height * .58 }, item.Id == nearbySwitch
                    ? Selection : control.TargetId is null ? Rgb(118, 121, 128) : Rgb(122, 192, 213));
            }
            else if (item.Component is SceneSprite sprite &&
                assets?.FirstOrDefault(asset => asset.Id == sprite.AssetId) is { } asset)
            {
                var (a, b, c, d) = Corners(t);
                sprites.Add(new RenderSprite(Image(asset), a, b, c, d, triangles.Count));
            }
            else if (item.Component is SceneSpawn)
            {
                Quad(t, Rgb(20, 56, 77));
                AtLocal(t, 0, 0, .25, .83, Rgb(117, 195, 242));
                AtLocal(t, 0, 0, .83, .25, Rgb(117, 195, 242));
            }
            else if (item.Component is SceneKey)
            {
                Quad(t, Rgb(129, 87, 29));
                AtLocal(t, -.18, 0, .33, .64, Rgb(255, 227, 140));
                AtLocal(t, .16, .12, .54, .23, Rgb(255, 227, 140));
            }
            else if (item.Component is SceneGoal goal)
            {
                var ready = play?.Progress is not { } progress || progress.Collected.Count >= goal.RequiredKeys;
                var edge = ready ? Rgb(103, 224, 174) : Rgb(156, 110, 172);
                Quad(t, ready ? Rgb(30, 81, 66) : Rgb(70, 46, 77));
                AtLocal(t, -.34, 0, .15, .72, edge);
                AtLocal(t, .34, 0, .15, .72, edge);
                AtLocal(t, 0, -.31, .83, .14, edge);
                AtLocal(t, 0, .31, .83, .14, edge);
            }
        }
        if (grid && entities.FirstOrDefault(item => item.Id == selected) is { Component: SceneSwitch links } source)
        {
            surface = foreground;
            foreach (var target in entities.Where(item => links.Targets(item.Id)))
            {
                var dx = target.Transform.X - source.Transform.X;
                var dy = target.Transform.Y - source.Transform.Y;
                Quad(new Transform2D(source.Transform.X + dx / 2, source.Transform.Y + dy / 2,
                    Math.Sqrt(dx * dx + dy * dy), .004, Math.Atan2(dy, dx) * 180 / Math.PI), Rgb(117, 194, 202));
            }
        }
        if (play?.Visitor is { } actor)
        {
            surface = foreground;
            Quad(new Transform2D(actor.X, actor.Y, .032, .054, 45), Rgb(47, 100, 148));
            Quad(new Transform2D(actor.X, actor.Y, .022, .037, 45), Rgb(125, 205, 243));
        }
        return new RenderSnapshot(projectId, revision, grid ? Rgb(12, 19, 25) : background, triangles,
            playSessionId: play?.SessionId, playStep: play?.Step, sprites: sprites, foreground: foreground);

        void AtLocal(Transform2D t, double x, double y, double width, double height, RenderColor color)
        {
            var angle = t.Rotation * Math.PI / 180;
            Quad(new Transform2D(t.X + x * t.Width * Math.Cos(angle) - y * t.Height * Math.Sin(angle),
                t.Y + x * t.Width * Math.Sin(angle) + y * t.Height * Math.Cos(angle),
                t.Width * width, t.Height * height, t.Rotation), color);
        }

        void Quad(Transform2D t, RenderColor color)
        {
            var (a, b, c, d) = Corners(t);
            surface.Add(new RenderTriangle(Vertex(a), Vertex(b), Vertex(c)));
            surface.Add(new RenderTriangle(Vertex(a), Vertex(c), Vertex(d)));
            RenderVertex Vertex(RenderPoint p) => new(p.X, p.Y, color);
        }

        (RenderPoint, RenderPoint, RenderPoint, RenderPoint) Corners(Transform2D t)
        {
            var angle = t.Rotation * Math.PI / 180;
            var cos = Math.Cos(angle);
            var sin = Math.Sin(angle);
            var a = Vertex(-t.Width / 2, -t.Height / 2);
            var b = Vertex(t.Width / 2, -t.Height / 2);
            var c = Vertex(t.Width / 2, t.Height / 2);
            var d = Vertex(-t.Width / 2, t.Height / 2);
            return (a, b, c, d);
            RenderPoint Vertex(double x, double y) => projection.ToViewport(t.X + x * cos - y * sin,
                t.Y + x * sin + y * cos);
        }
    }

    private static RenderPoint Place(Transform2D transform, double x, double y)
    {
        var angle = transform.Rotation * Math.PI / 180;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        return new RenderPoint((float)Math.Clamp(transform.X + x * cos - y * sin, .012, .988),
            (float)Math.Clamp(transform.Y + x * sin + y * cos, .018, .982));
    }

    private static RenderColor Rgb(byte r, byte g, byte b) => RenderColor.FromRgb(r, g, b);
}
