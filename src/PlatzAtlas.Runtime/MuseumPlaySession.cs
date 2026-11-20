using System.Diagnostics.CodeAnalysis;
using PlatzAtlas.Core;

namespace PlatzAtlas.Runtime;

public sealed record PlayRoomSnapshot(Guid SessionId, Guid ProjectId, Guid SceneId, long SourceRevision,
    IReadOnlyList<Lantern> Objects, long Step = 0)
{
    public RoomPosition Visitor { get; init; } = new(0.5, 0.92);
    public IReadOnlyList<SceneEntity>? Entities { get; init; }
    public RoomProgress? Progress { get; init; }
}

public readonly record struct RoomPosition(double X, double Y);
public sealed record RoomProgress(int TotalKeys, IReadOnlyList<Guid> Collected, int? GoalRequirement, string? CompletedGoal)
{
    public string Summary => CompletedGoal is not null ? $"Goal complete · {CompletedGoal}" :
        $"Keys {Collected.Count}/{TotalKeys}" + (GoalRequirement is > 0 ? $" · Goal needs {GoalRequirement}" : "");
}
public sealed record PlaySwitchMarker(Guid SwitchId, string Name, RoomPosition Position);
public enum WalkDirection { Left, Right, Up, Down }

public sealed record PlaySwitchResult(long Step, Guid SwitchId, string SwitchName, Guid TargetId,
    string TargetLocation, bool WasOn, bool IsOn)
{
    public bool IsDoor { get; init; }
    public PlayTargetChange? AdditionalTarget { get; init; }
    public string StateText => IsDoor ? $"{(WasOn ? "Open" : "Closed")} → {(IsOn ? "Open" : "Closed")}"
        : $"{(WasOn ? "On" : "Off")} → {(IsOn ? "On" : "Off")}";
    public string Summary => AdditionalTarget is { } extra ? $"{TargetLocation} · {StateText}\n{extra.Name} · {extra.StateText}" :
        $"{TargetLocation} · {StateText}";
}
public sealed record PlayTargetChange(Guid Id, string Name, bool IsDoor, bool WasOn, bool IsOn)
{
    public string StateText => $"{State(WasOn)} → {State(IsOn)}";
    private string State(bool value) => IsDoor ? value ? "Open" : "Closed" : value ? "On" : "Off";
}

// The runtime owns only detached scene state and has no reference to the authoring session.
public sealed partial class MuseumPlaySession
{
    private readonly Dictionary<Guid, int> _objectIndices;
    private readonly Dictionary<Guid, LightSwitch> _switches;
    private bool _stopped;

    public MuseumPlaySession(MuseumProject snapshot) : this(RuntimeRoom.FromProject(snapshot)) { }

    public MuseumPlaySession(RuntimeRoom snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var lanterns = snapshot.Objects.ToArray();
        _objectIndices = lanterns.Select((lantern, index) => (lantern.Id, index))
            .ToDictionary(item => item.Id, item => item.index);
        Switches = Array.AsReadOnly(snapshot.Switches.ToArray());
        _switches = Switches.ToDictionary(item => item.Id);
        if (_switches.Values.Any(item => !_objectIndices.ContainsKey(item.TargetId)))
            throw new InvalidDataException("The play snapshot contains a switch without a lantern target.");

        Markers = Array.AsReadOnly(Switches.Select(item => new PlaySwitchMarker(item.Id, item.Name,
            new RoomPosition(Math.Clamp(lanterns[_objectIndices[item.TargetId]].X, 0.14, 0.86), 0.80))).ToArray());
        Current = new PlayRoomSnapshot(Guid.NewGuid(), snapshot.ProjectId, snapshot.SceneId,
            snapshot.SourceRevision, Array.AsReadOnly(lanterns));
        if (snapshot.Entities is { } entities)
        {
            SceneValidation.ValidateEntities(entities, snapshot.ProjectId, snapshot.SceneId);
            var progress = entities.Any(SceneValidation.RequiresSchemaFive)
                ? new RoomProgress(entities.Count(item => item.Component is SceneKey), [],
                    entities.Select(item => item.Component).OfType<SceneGoal>().Select(item => (int?)item.RequiredKeys).Min(), null)
                : null;
            Current = ReadContacts(Current with
            {
                Entities = Array.AsReadOnly(entities.ToArray()), Visitor = FindSpawn(entities), Progress = progress
            }, entities);
            Markers = Array.AsReadOnly(entities.Where(item => item.Component is SceneSwitch { TargetId: not null })
                .Select(item => new PlaySwitchMarker(item.Id, item.Name, new(item.Transform.X, item.Transform.Y))).ToArray());
        }
    }

    public IReadOnlyList<LightSwitch> Switches { get; }
    public IReadOnlyList<PlaySwitchMarker> Markers { get; }
    public PlayRoomSnapshot Current { get; private set; }

    public PlaySwitchMarker? NearbySwitch => _stopped || Current.Progress?.CompletedGoal is not null ? null : Markers
        .Select(marker => (Marker: marker, Distance: DistanceSquared(Current.Visitor, marker.Position)))
        .Where(item => item.Distance <= 0.095 * 0.095)
        .OrderBy(item => item.Distance).ThenBy(item => item.Marker.SwitchId).Select(item => item.Marker).FirstOrDefault();

    public bool TryWalk(Guid sessionId, WalkDirection direction)
    {
        if (_stopped || Current.Progress?.CompletedGoal is not null || sessionId != Current.SessionId)
            return false;
        var (x, y) = direction switch
        {
            WalkDirection.Left => (-0.04, 0d),
            WalkDirection.Right => (0.04, 0d),
            WalkDirection.Up => (0d, -0.04),
            WalkDirection.Down => (0d, 0.04),
            _ => (0d, 0d)
        };
        if (Current.Entities is { } entities)
            return TryWalkScene(x, y, entities);
        var next = new RoomPosition(Math.Clamp(Current.Visitor.X + x, 0.12, 0.88),
            Math.Clamp(Current.Visitor.Y + y, 0.78, 0.96));
        if (next == Current.Visitor)
            return false;
        Current = Current with { Visitor = next };
        return true;
    }

    public bool TryUseNearbySwitch(Guid sessionId, [NotNullWhen(true)] out PlaySwitchResult? result)
    {
        result = null;
        return NearbySwitch is { } marker && TryUseSwitch(sessionId, marker.SwitchId, out result);
    }

    public bool TryUseSwitch(Guid sessionId, Guid switchId, [NotNullWhen(true)] out PlaySwitchResult? result)
    {
        result = null;
        if (_stopped || sessionId != Current.SessionId)
            return false;
        if (Current.Entities is { } entities)
            return TryUseSceneSwitch(switchId, entities, out result);
        if (!_switches.TryGetValue(switchId, out var item))
            return false;

        var index = _objectIndices[item.TargetId];
        var original = Current.Objects[index];
        var updated = original with { IsOn = item.Behavior == SwitchBehavior.TurnOn || !original.IsOn };
        var nextObjects = Current.Objects.ToArray();
        nextObjects[index] = updated;
        var step = Current.Step + 1;
        Current = Current with { Objects = Array.AsReadOnly(nextObjects), Step = step };
        result = new PlaySwitchResult(step, item.Id, item.Name, original.Id, original.Location,
            original.IsOn, updated.IsOn);
        return true;
    }

    public void Stop() => _stopped = true;

    private static double DistanceSquared(RoomPosition a, RoomPosition b) =>
        (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);
}
