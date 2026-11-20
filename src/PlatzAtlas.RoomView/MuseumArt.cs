using PlatzAtlas.Core;
using PlatzAtlas.Rendering;
using PlatzAtlas.Runtime;

namespace PlatzAtlas.RoomView;

public static class MuseumArt
{
    private static readonly RenderColor Background = Ink(14, 25, 29);
    private static readonly RenderColor Highlight = Ink(240, 196, 134);
    private static readonly RenderColor PreviewHighlight = Ink(133, 217, 198);
    private static readonly RenderColor PlayHighlight = Ink(169, 213, 255);

    public static RenderSnapshot Create(MuseumProject project, Guid? selectedId, LightPreview? preview, PlayRoomSnapshot? play) =>
        Draw(play?.Objects ?? project.Objects, selectedId, preview, play,
            play?.ProjectId ?? project.ProjectId, play?.SourceRevision ?? project.Revision);

    public static RenderSnapshot CreatePlayer(MuseumPlaySession session, Guid? activeTarget) =>
        Draw(session.Current.Objects, activeTarget, null, session.Current, session.Current.ProjectId,
            session.Current.SourceRevision, session.Markers, session.NearbySwitch?.SwitchId);

    private static RenderSnapshot Draw(IReadOnlyList<Lantern> objects, Guid? selectedId, LightPreview? preview,
        PlayRoomSnapshot? play, Guid projectId, long revision, IReadOnlyList<PlaySwitchMarker>? markers = null,
        Guid? nearbySwitch = null)
    {
        var scene = new Drawing();
        var selectionHighlight = play is null ? Highlight : PlayHighlight;
        DrawRoom(scene);
        foreach (var lantern in objects)
        {
            if (preview is not null)
            {
                if (lantern.Id == preview.After.Id)
                    DrawLantern(scene, preview.After, PreviewHighlight);
                else
                    DrawLantern(scene, lantern, null);
            }
            else
                DrawLantern(scene, lantern, lantern.Id == selectedId ? selectionHighlight : null);
        }

        if (markers is not null && play is not null)
        {
            foreach (var marker in markers)
            {
                var (x, y) = marker.Position;
                scene.Ellipse(x, y, 0.018, 0.025, Ink(19, 33, 41));
                scene.Ellipse(x, y, 0.012, 0.017, marker.SwitchId == nearbySwitch ? Highlight : PlayHighlight, 4);
            }
            var (visitorX, visitorY) = play.Visitor;
            scene.Ellipse(visitorX, visitorY, 0.018, 0.028, Ink(14, 29, 37));
            scene.Ellipse(visitorX, visitorY, 0.013, 0.021, PlayHighlight);
            scene.Ellipse(visitorX, visitorY, 0.006, 0.010, Ink(225, 242, 255));
        }

        return new RenderSnapshot(projectId, revision, Background, scene.Triangles, preview?.After.Id, play?.SessionId, play?.Step);
    }

    private static void DrawRoom(Drawing scene)
    {
        scene.Rectangle(0, 0.10, 1, 0.74, Ink(27, 45, 49));
        scene.Rectangle(0.11, 0.17, 0.89, 0.71, Ink(31, 50, 55));
        scene.Quad(Ink(21, 36, 40), (0, 0.10), (0.11, 0.17), (0.11, 0.71), (0, 0.82));
        scene.Quad(Ink(18, 33, 39), (0.89, 0.17), (1, 0.10), (1, 0.82), (0.89, 0.71));
        scene.Quad(Ink(34, 45, 44), (0, 0.82), (0.11, 0.71), (0.89, 0.71), (1, 0.82));
        scene.Rectangle(0, 0.82, 1, 1, Ink(30, 41, 41));
        scene.Rectangle(0.11, 0.69, 0.89, 0.713, Ink(47, 62, 59));
        scene.Rectangle(0.11, 0.174, 0.89, 0.18, Ink(61, 79, 74));

        foreach (var x in new[] { 0.12, 0.37, 0.625, 0.87 })
        {
            scene.Rectangle(x, 0.18, x + 0.01, 0.69, Ink(38, 60, 62));
            scene.Rectangle(x + 0.01, 0.18, x + 0.013, 0.69, Ink(49, 68, 67));
        }
        foreach (var step in new[] { 0.76, 0.83, 0.94 })
            scene.Rectangle(0, step, 1, step + 0.002, Ink(43, 55, 52));
        for (var step = 1; step < 8; step++)
        {
            var x = step / 8d;
            var baseX = Math.Clamp(0.5 + (x - 0.5) * 1.6, 0, 0.996);
            scene.Quad(Ink(42, 54, 51), (x, 0.712), (x + 0.002, 0.712),
                (baseX + 0.004, 1), (baseX, 1));
        }

        // A small lunar display gives both lights a shared gallery context.
        scene.Rectangle(0.395, 0.237, 0.605, 0.617, Ink(18, 32, 37));
        scene.Rectangle(0.401, 0.242, 0.599, 0.608, Ink(94, 104, 89));
        scene.Rectangle(0.407, 0.25, 0.593, 0.6, Ink(21, 36, 43));
        scene.Ellipse(0.5, 0.414, 0.074, 0.122, Ink(61, 83, 83));
        scene.Ellipse(0.5, 0.414, 0.072, 0.119, Ink(21, 36, 43));
        scene.Ellipse(0.5, 0.414, 0.049, 0.083, Ink(113, 143, 138));
        scene.Ellipse(0.491, 0.405, 0.039, 0.073, Ink(156, 177, 160));
        scene.Ellipse(0.476, 0.382, 0.009, 0.014, Ink(128, 152, 142));
        scene.Ellipse(0.504, 0.444, 0.014, 0.021, Ink(127, 151, 142));
        scene.Ellipse(0.512, 0.39, 0.007, 0.013, Ink(131, 154, 145));
        scene.Ellipse(0.553, 0.326, 0.005, 0.01, Ink(183, 198, 172), 4);
        scene.Ellipse(0.44, 0.511, 0.003, 0.006, Ink(137, 161, 152), 4);
        scene.Rectangle(0.467, 0.555, 0.533, 0.56, Ink(90, 110, 103));
        scene.Rectangle(0.48, 0.569, 0.52, 0.573, Ink(68, 91, 90));
        scene.Rectangle(0.47, 0.635, 0.53, 0.655, Ink(76, 88, 77));
    }

    private static void DrawLantern(Drawing scene, Lantern lantern, RenderColor? highlight)
    {
        var x = lantern.X;
        var y = lantern.Y;
        if (lantern.IsOn)
        {
            scene.Ellipse(x, y - 0.035, 0.107, 0.224, Ink(47, 56, 47));
            scene.Ellipse(x, y - 0.035, 0.086, 0.194, Ink(65, 64, 47));
            scene.Ellipse(x, y - 0.035, 0.063, 0.149, Ink(89, 77, 49));
            scene.Ellipse(x, y + 0.22, 0.139, 0.069, Ink(67, 63, 45));
            scene.Ellipse(x, y + 0.207, 0.095, 0.045, Ink(89, 76, 48));
        }

        scene.Ellipse(x, y + 0.216, 0.068, 0.025, Ink(22, 33, 33));
        scene.Quad(Ink(50, 64, 61), (x - 0.043, y + 0.068), (x + 0.043, y + 0.068),
            (x + 0.058, y + 0.092), (x - 0.058, y + 0.092));
        scene.Rectangle(x - 0.058, y + 0.092, x + 0.058, y + 0.204, Ink(39, 52, 51));
        scene.Rectangle(x - 0.058, y + 0.093, x + 0.042, y + 0.202, Ink(47, 62, 58));
        scene.Rectangle(x - 0.032, y + 0.124, x + 0.019, y + 0.153, Ink(83, 91, 76));
        scene.Rectangle(x - 0.028, y + 0.133, x + 0.015, y + 0.137, Ink(132, 135, 106));

        var metal = lantern.IsOn ? Ink(205, 151, 81) : Ink(100, 121, 111);
        scene.Ellipse(x, y - 0.13, 0.017, 0.027, metal);
        scene.Ellipse(x, y - 0.13, 0.011, 0.018, lantern.IsOn ? Ink(89, 77, 49) : Ink(31, 50, 55));
        scene.Quad(metal, (x - 0.019, y - 0.12), (x + 0.019, y - 0.12),
            (x + 0.039, y - 0.077), (x - 0.039, y - 0.077));
        scene.Quad(Ink(27, 38, 39), (x - 0.032, y - 0.078), (x + 0.032, y - 0.078),
            (x + 0.039, y + 0.044), (x - 0.039, y + 0.044));
        scene.Quad(lantern.IsOn ? Ink(237, 175, 89) : Ink(46, 68, 70),
            (x - 0.025, y - 0.069), (x + 0.025, y - 0.069), (x + 0.031, y + 0.037), (x - 0.031, y + 0.037));
        if (lantern.IsOn)
        {
            scene.Ellipse(x, y - 0.009, 0.018, 0.041, Ink(255, 216, 136));
            scene.Ellipse(x, y + 0.001, 0.01, 0.028, Ink(255, 241, 188));
        }
        scene.Rectangle(x - 0.002, y - 0.075, x + 0.002, y + 0.043, metal);
        scene.Rectangle(x - 0.041, y + 0.044, x + 0.041, y + 0.065, metal);

        if (highlight is not { } marker)
            return;

        foreach (var edge in new[] { x - 0.065, x + 0.062 })
        {
            var markerLeft = edge < x ? edge : edge - 0.015;
            scene.Rectangle(edge, y - 0.169, edge + 0.003, y - 0.131, marker);
            scene.Rectangle(edge, y + 0.062, edge + 0.003, y + 0.100, marker);
            scene.Rectangle(markerLeft, y - 0.169, markerLeft + 0.018, y - 0.164, marker);
            scene.Rectangle(markerLeft, y + 0.095, markerLeft + 0.018, y + 0.100, marker);
        }
        scene.Ellipse(x, y + 0.264, 0.009, 0.015, marker, 4);
    }

    private static RenderColor Ink(byte r, byte g, byte b) => RenderColor.FromRgb(r, g, b);

    private sealed class Drawing
    {
        public List<RenderTriangle> Triangles { get; } = new();

        public void Rectangle(double left, double top, double right, double bottom, RenderColor color) =>
            Quad(color, (left, top), (right, top), (right, bottom), (left, bottom));

        public void Quad(RenderColor color, (double X, double Y) a, (double X, double Y) b,
            (double X, double Y) c, (double X, double Y) d)
        {
            Triangle(color, a, b, c);
            Triangle(color, a, c, d);
        }

        public void Ellipse(double x, double y, double radiusX, double radiusY, RenderColor color, int segments = 24)
        {
            var previous = (x + radiusX, y);
            for (var step = 1; step <= segments; step++)
            {
                var angle = step * Math.Tau / segments;
                var next = (x + radiusX * Math.Cos(angle), y + radiusY * Math.Sin(angle));
                Triangle(color, (x, y), previous, next);
                previous = next;
            }
        }

        private void Triangle(RenderColor color, (double X, double Y) a, (double X, double Y) b,
            (double X, double Y) c)
        {
            Triangles.Add(new RenderTriangle(Vertex(a), Vertex(b), Vertex(c)));
            RenderVertex Vertex((double X, double Y) point) => new((float)point.X, (float)point.Y, color);
        }
    }
}
