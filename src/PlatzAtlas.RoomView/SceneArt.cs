using PlatzAtlas.Core;
using PlatzAtlas.Rendering;
using PlatzAtlas.Runtime;

namespace PlatzAtlas.RoomView;

public static class SceneArt
{
    private static readonly RenderColor Background = Rgb(17, 25, 33);
    private static readonly RenderColor Grid = Rgb(32, 44, 53);
    private static readonly RenderColor Selection = Rgb(255, 214, 152);

    public static IEnumerable<SceneEntity> PaintOrder(IReadOnlyList<SceneEntity> entities) =>
        entities.OrderBy(item => item.Component is WallPanel ? 0 : 1).ThenBy(item => item.Id);

    public static RenderSnapshot Create(Guid projectId, long revision, IReadOnlyList<SceneEntity> entities,
        Guid? selected, bool grid = false, PlayRoomSnapshot? play = null, Guid? nearbySwitch = null)
    {
        var triangles = new List<RenderTriangle>();
        if (grid)
        {
            for (var index = 1; index < 20; index++)
                Quad(new Transform2D(index / 20.0, .5, .001, 1, 0), Grid);
            for (var index = 1; index < 12; index++)
                Quad(new Transform2D(.5, index / 12.0, 1, .0015, 0), Grid);
        }

        foreach (var item in PaintOrder(entities))
        {
            var t = item.Transform;
            if (item.Id == selected)
            {
                AtLocal(t, -.508, 0, .025, 1.026, Selection);
                AtLocal(t, .508, 0, .025, 1.026, Selection);
                AtLocal(t, 0, -.508, 1.026, .025, Selection);
                AtLocal(t, 0, .508, 1.026, .025, Selection);
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
        }
        if (play?.Visitor is { } actor)
        {
            Quad(new Transform2D(actor.X, actor.Y, .032, .054, 45), Rgb(47, 100, 148));
            Quad(new Transform2D(actor.X, actor.Y, .022, .037, 45), Rgb(125, 205, 243));
        }
        return new RenderSnapshot(projectId, revision, Background, triangles,
            playSessionId: play?.SessionId, playStep: play?.Step);

        void AtLocal(Transform2D t, double x, double y, double width, double height, RenderColor color)
        {
            var angle = t.Rotation * Math.PI / 180;
            Quad(new Transform2D(t.X + x * t.Width * Math.Cos(angle) - y * t.Height * Math.Sin(angle),
                t.Y + x * t.Width * Math.Sin(angle) + y * t.Height * Math.Cos(angle),
                t.Width * width, t.Height * height, t.Rotation), color);
        }

        void Quad(Transform2D t, RenderColor color)
        {
            var angle = t.Rotation * Math.PI / 180;
            var cos = Math.Cos(angle);
            var sin = Math.Sin(angle);
            var a = Vertex(-t.Width / 2, -t.Height / 2);
            var b = Vertex(t.Width / 2, -t.Height / 2);
            var c = Vertex(t.Width / 2, t.Height / 2);
            var d = Vertex(-t.Width / 2, t.Height / 2);
            triangles.Add(new RenderTriangle(a, b, c));
            triangles.Add(new RenderTriangle(a, c, d));
            RenderVertex Vertex(double x, double y) => new((float)(t.X + x * cos - y * sin),
                (float)(t.Y + x * sin + y * cos), color);
        }
    }

    private static RenderColor Rgb(byte r, byte g, byte b) => RenderColor.FromRgb(r, g, b);
}
