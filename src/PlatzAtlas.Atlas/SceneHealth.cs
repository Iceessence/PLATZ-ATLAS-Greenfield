using System.Globalization;

namespace PlatzAtlas.Atlas;

public sealed record SceneDiagnostic(string Code, string Message, IReadOnlyList<Guid> ObjectIds);
public sealed record SceneHealthReport(ProjectReadScope Scope, string Sha256, IReadOnlyList<SceneDiagnostic> Issues)
{
    internal static SceneHealthReport Inspect(SceneEvidence evidence, string sha)
    {
        var issues = new List<SceneDiagnostic>();
        foreach (var source in evidence.Objects)
        {
            if (source is { Kind: SceneSourceKind.Switch, TargetId: null })
                issues.Add(new("unlinked-switch", $"{source.Name} has no target.", [source.Id]));
            if (source is { Kind: SceneSourceKind.Spawn, SpawnBlocked: true })
                issues.Add(new("blocked-spawn", $"{source.Name} overlaps collision or the room edge.", [source.Id]));
        }
        foreach (var group in evidence.Objects.GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            var ids = group.Select(item => item.Id).ToArray();
            if (ids.Length > 1)
                issues.Add(new("duplicate-name", $"{ids.Length} objects use “{group.Key}”. Select one or use an exact ID.", ids));
        }
        var keys = evidence.Objects.Count(item => item.Kind == SceneSourceKind.Key);
        foreach (var source in evidence.Objects.Where(item => item.Kind == SceneSourceKind.Goal && item.RequiredKeys > keys))
            issues.Add(new("missing-keys", $"{source.Name} requires {source.RequiredKeys} keys. The room has {keys}.", [source.Id]));
        return new SceneHealthReport(evidence.Scope, sha, Array.AsReadOnly(issues.ToArray()));
    }
}

public static class SceneSourceResolver
{
    public static IReadOnlyList<SceneSource> Resolve(SceneEvidence evidence, string reference, Guid? selected = null)
    {
        var name = reference.Trim();
        if (Guid.TryParse(name, out var id))
            return evidence.Objects.Where(item => item.Id == id).Take(2).ToArray();
        if (name.StartsWith('#') && long.TryParse(name.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            return evidence.Objects.Where(item => item.Number == number).Take(2).ToArray();
        if (name.Equals("selection", StringComparison.OrdinalIgnoreCase) || name.Equals("this object", StringComparison.OrdinalIgnoreCase))
            return evidence.Objects.Where(item => item.Id == selected).ToArray();
        return evidence.Objects.Where(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
    }
}
