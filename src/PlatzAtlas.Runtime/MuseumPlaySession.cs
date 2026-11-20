using System.Diagnostics.CodeAnalysis;
using PlatzAtlas.Core;

namespace PlatzAtlas.Runtime;

public sealed record PlayRoomSnapshot(Guid SessionId, Guid ProjectId, Guid SceneId, long SourceRevision,
    IReadOnlyList<Lantern> Objects, long Step = 0);

public sealed record PlaySwitchResult(long Step, Guid SwitchId, string SwitchName, Guid TargetId,
    string TargetLocation, bool WasOn, bool IsOn);

// The runtime owns only detached scene state and has no reference to the authoring session.
public sealed class MuseumPlaySession
{
    private readonly Dictionary<Guid, int> _objectIndices;
    private readonly Dictionary<Guid, LightSwitch> _switches;
    private bool _stopped;

    public MuseumPlaySession(MuseumProject snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var lanterns = snapshot.Objects.ToArray();
        _objectIndices = lanterns.Select((lantern, index) => (lantern.Id, index))
            .ToDictionary(item => item.Id, item => item.index);
        Switches = Array.AsReadOnly(snapshot.Switches.ToArray());
        _switches = Switches.ToDictionary(item => item.Id);
        if (_switches.Values.Any(item => !_objectIndices.ContainsKey(item.TargetId)))
            throw new InvalidDataException("The play snapshot contains a switch without a lantern target.");

        Current = new PlayRoomSnapshot(Guid.NewGuid(), snapshot.ProjectId, snapshot.SceneId,
            snapshot.Revision, Array.AsReadOnly(lanterns));
    }

    public IReadOnlyList<LightSwitch> Switches { get; }
    public PlayRoomSnapshot Current { get; private set; }

    public bool TryUseSwitch(Guid sessionId, Guid switchId, [NotNullWhen(true)] out PlaySwitchResult? result)
    {
        result = null;
        if (_stopped || sessionId != Current.SessionId || !_switches.TryGetValue(switchId, out var item))
            return false;

        var index = _objectIndices[item.TargetId];
        var original = Current.Objects[index];
        var updated = original with { IsOn = !original.IsOn };
        var nextObjects = Current.Objects.ToArray();
        nextObjects[index] = updated;
        var step = Current.Step + 1;
        Current = Current with { Objects = Array.AsReadOnly(nextObjects), Step = step };
        result = new PlaySwitchResult(step, item.Id, item.Name, original.Id, original.Location,
            original.IsOn, updated.IsOn);
        return true;
    }

    public void Stop() => _stopped = true;
}
