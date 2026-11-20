using System.Diagnostics.CodeAnalysis;
using PlatzAtlas.Core;

namespace PlatzAtlas.Runtime;

public sealed partial class MuseumPlaySession
{
    private const double VisitorRadius = .018;

    internal static RoomPosition FindSpawn(IReadOnlyList<SceneEntity> entities)
    {
        var preferred = new RoomPosition(.50, .90);
        if (!Blocked(entities, preferred))
            return preferred;
        var candidates = from y in Enumerable.Range(1, 19)
                         from x in Enumerable.Range(1, 19)
                         let point = new RoomPosition(x / 20.0, y / 20.0)
                         orderby DistanceSquared(point, preferred), y, x
                         select point;
        foreach (var point in candidates)
            if (!Blocked(entities, point))
                return point;
        throw new InvalidDataException("The room has no clear spawn. Leave an open area near the bottom of the room.");
    }

    private bool TryWalkScene(double x, double y, IReadOnlyList<SceneEntity> entities)
    {
        var next = new RoomPosition(Math.Clamp(Current.Visitor.X + x, VisitorRadius, 1 - VisitorRadius),
            Math.Clamp(Current.Visitor.Y + y, VisitorRadius, 1 - VisitorRadius));
        if (next == Current.Visitor || Blocked(entities, next))
            return false;
        Current = Current with { Visitor = next };
        return true;
    }

    private bool TryUseSceneSwitch(Guid switchId, IReadOnlyList<SceneEntity> entities,
        [NotNullWhen(true)] out PlaySwitchResult? result)
    {
        result = null;
        if (NearbySwitch?.SwitchId != switchId ||
            entities.FirstOrDefault(item => item.Id == switchId) is not { Component: SceneSwitch control } source ||
            entities.FirstOrDefault(item => item.Id == control.TargetId) is not { } target)
            return false;

        var door = target.Component as SceneDoor;
        var before = door?.IsOpen ?? ((SceneLamp)target.Component).IsOn;
        var after = control.Behavior == SwitchBehavior.TurnOn || !before;
        // Keep a door open if closing would trap the visitor inside its collision shape.
        if (door is not null && !after &&
            target.Transform.IntersectsCircle(Current.Visitor.X, Current.Visitor.Y, VisitorRadius))
            after = true;
        SceneComponent next = door is null ? new SceneLamp(after) : new SceneDoor(after);
        var step = Current.Step + 1;
        Current = Current with
        {
            Step = step,
            Entities = Array.AsReadOnly(entities.Select(item =>
                item.Id == target.Id ? item with { Component = next } : item).ToArray())
        };
        result = new PlaySwitchResult(step, source.Id, source.Name, target.Id, target.Name, before, after)
        {
            IsDoor = door is not null
        };
        return true;
    }

    private static bool Blocked(IReadOnlyList<SceneEntity> entities, RoomPosition point) => entities.Any(item =>
        item.Component is WallPanel or SceneDoor { IsOpen: false } &&
        item.Transform.IntersectsCircle(point.X, point.Y, VisitorRadius));
}
