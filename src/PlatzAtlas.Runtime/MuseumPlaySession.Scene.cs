using System.Diagnostics.CodeAnalysis;
using PlatzAtlas.Core;

namespace PlatzAtlas.Runtime;

public sealed partial class MuseumPlaySession
{
    private const double VisitorRadius = .018;

    internal static RoomPosition FindSpawn(IReadOnlyList<SceneEntity> entities)
    {
        var availableKeys = entities.Count(item => item.Component is SceneKey);
        if (entities.Any(item => item.Component is SceneGoal goal && goal.RequiredKeys > availableKeys))
            throw new InvalidDataException("A goal requires more keys than the room contains.");
        if (entities.SingleOrDefault(item => item.Component is SceneSpawn) is { } spawn)
        {
            var authored = new RoomPosition(spawn.Transform.X, spawn.Transform.Y);
            if (SpawnBlocked(entities, authored))
                throw new InvalidDataException($"Move {spawn.Name} clear of walls, closed doors and the room edge.");
            return authored;
        }
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
        Current = ReadContacts(Current with { Visitor = next }, entities);
        return true;
    }

    private static PlayRoomSnapshot ReadContacts(PlayRoomSnapshot snapshot, IReadOnlyList<SceneEntity> entities)
    {
        if (snapshot.Progress is not { } progress)
            return snapshot;
        var visitor = snapshot.Visitor;
        var picked = entities.Where(item => item.Component is SceneKey && !progress.Collected.Contains(item.Id) &&
            item.Transform.IntersectsCircle(visitor.X, visitor.Y, VisitorRadius)).Select(item => item.Id).ToArray();
        var collected = picked.Length == 0 ? progress.Collected : Array.AsReadOnly(progress.Collected.Concat(picked).ToArray());
        var goal = entities.FirstOrDefault(item => item.Component is SceneGoal condition &&
            collected.Count >= condition.RequiredKeys && item.Transform.IntersectsCircle(visitor.X, visitor.Y, VisitorRadius));
        return snapshot with { Progress = progress with { Collected = collected, CompletedGoal = goal?.Name } };
    }

    private bool TryUseSceneSwitch(Guid switchId, IReadOnlyList<SceneEntity> entities,
        [NotNullWhen(true)] out PlaySwitchResult? result)
    {
        result = null;
        if (NearbySwitch?.SwitchId != switchId ||
            entities.FirstOrDefault(item => item.Id == switchId) is not { Component: SceneSwitch control } source ||
            entities.FirstOrDefault(item => item.Id == control.TargetId) is not { } target)
            return false;

        var first = SwitchChange(target, control.Behavior);
        var second = control.SecondTargetId is Guid additional
            ? SwitchChange(entities.Single(item => item.Id == additional), control.Behavior) : null;
        var step = Current.Step + 1;
        Current = Current with
        {
            Step = step,
            Entities = Array.AsReadOnly(entities.Select(item => item.Id == first.Id ? Apply(item, first)
                : second is not null && item.Id == second.Id ? Apply(item, second) : item).ToArray())
        };
        result = new PlaySwitchResult(step, source.Id, source.Name, target.Id, target.Name, first.WasOn, first.IsOn)
        {
            IsDoor = first.IsDoor, AdditionalTarget = second
        };
        return true;

        static SceneEntity Apply(SceneEntity item, PlayTargetChange change) => item with
        {
            Component = change.IsDoor ? new SceneDoor(change.IsOn) : new SceneLamp(change.IsOn)
        };
    }

    private PlayTargetChange SwitchChange(SceneEntity target, SwitchBehavior behavior)
    {
        var door = target.Component as SceneDoor;
        var before = door?.IsOpen ?? ((SceneLamp)target.Component).IsOn;
        var after = behavior == SwitchBehavior.TurnOn || !before;
        if (door is not null && !after &&
            target.Transform.IntersectsCircle(Current.Visitor.X, Current.Visitor.Y, VisitorRadius))
            after = true;
        return new PlayTargetChange(target.Id, target.Name, door is not null, before, after);
    }

    public static bool SpawnBlocked(IReadOnlyList<SceneEntity> entities, RoomPosition point) =>
        point.X < VisitorRadius || point.X > 1 - VisitorRadius || point.Y < VisitorRadius || point.Y > 1 - VisitorRadius ||
        Blocked(entities, point);

    private static bool Blocked(IReadOnlyList<SceneEntity> entities, RoomPosition point) => entities.Any(item =>
        item.Component is WallPanel or SceneDoor { IsOpen: false } &&
        item.Transform.IntersectsCircle(point.X, point.Y, VisitorRadius));
}
