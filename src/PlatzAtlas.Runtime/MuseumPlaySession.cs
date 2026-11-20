using System.Diagnostics.CodeAnalysis;
using PlatzAtlas.Core;

namespace PlatzAtlas.Runtime;

public sealed record PlayRoomSnapshot(Guid SessionId, Guid ProjectId, Guid SceneId, long SourceRevision,
    IReadOnlyList<Lantern> Objects, long Step = 0)
{
    public RoomPosition Visitor { get; init; } = new(0.5, 0.92);
}

public readonly record struct RoomPosition(double X, double Y);
public sealed record PlaySwitchMarker(Guid SwitchId, string Name, RoomPosition Position);
public enum WalkDirection { Left, Right, Up, Down }

public sealed record PlaySwitchResult(long Step, Guid SwitchId, string SwitchName, Guid TargetId,
    string TargetLocation, bool WasOn, bool IsOn);

// The runtime owns only detached scene state and has no reference to the authoring session.
public sealed class MuseumPlaySession
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
    }

    public IReadOnlyList<LightSwitch> Switches { get; }
    public IReadOnlyList<PlaySwitchMarker> Markers { get; }
    public PlayRoomSnapshot Current { get; private set; }

    public PlaySwitchMarker? NearbySwitch => _stopped ? null : Markers
        .Select(marker => (Marker: marker, Distance: DistanceSquared(Current.Visitor, marker.Position)))
        .Where(item => item.Distance <= 0.095 * 0.095)
        .OrderBy(item => item.Distance).ThenBy(item => item.Marker.SwitchId).Select(item => item.Marker).FirstOrDefault();

    public bool TryWalk(Guid sessionId, WalkDirection direction)
    {
        if (_stopped || sessionId != Current.SessionId)
            return false;
        var (x, y) = direction switch
        {
            WalkDirection.Left => (-0.04, 0d),
            WalkDirection.Right => (0.04, 0d),
            WalkDirection.Up => (0d, -0.04),
            WalkDirection.Down => (0d, 0.04),
            _ => (0d, 0d)
        };
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
        if (_stopped || sessionId != Current.SessionId || !_switches.TryGetValue(switchId, out var item))
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
