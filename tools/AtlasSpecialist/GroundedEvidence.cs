using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PlatzAtlas.Atlas;

internal sealed record RoomFixture(ProjectReadScope Scope, byte[] Snapshot, Dictionary<string, SceneSource> Objects);
internal sealed record Prompt(string Room, string Label, string Source, string Selection, string Category, string Text);
internal sealed record Grounding(SceneSource Object, string Text, string Sha256, Guid[] LinkedIds);

internal static partial class GroundedEvidence
{
    [GeneratedRegex(@"\b(?:selection|this object|the selected object)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Selection();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    public static Dictionary<string, RoomFixture> ReadRooms(string path)
    {
        return Rows(path, 5).GroupBy(fields => fields[0], StringComparer.Ordinal).ToDictionary(group => group.Key, group =>
        {
            var scope = new ProjectReadScope(Id(group.Key + "/project"), Id(group.Key + "/scene"), 5);
            var objects = group.ToDictionary(fields => fields[1], fields =>
            {
                var kind = Enum.Parse<SceneSourceKind>(fields[2]);
                return new SceneSource(Id(group.Key + "/" + fields[1]), fields[3], kind, .5, .45, .2, .1, 0,
                    kind is SceneSourceKind.Door or SceneSourceKind.Lamp ? true : null,
                    fields[4] == "-" ? null : Id(group.Key + "/" + fields[4]),
                    kind == SceneSourceKind.Switch ? "Toggle" : null, null, null, kind == SceneSourceKind.Wall ? "Teal" : null)
                {
                    RequiredKeys = kind == SceneSourceKind.Goal ? 1 : null
                };
            }, StringComparer.Ordinal);
            return new RoomFixture(scope, SceneEvidenceProfile.Publish(scope, objects.Values.ToArray()), objects);
        }, StringComparer.Ordinal);
    }

    public static Prompt[] ReadPrompts(string path) =>
        Rows(path, 6).Select(fields => new Prompt(fields[0], fields[1], fields[2], fields[3], fields[4], fields[5])).ToArray();

    private static IEnumerable<string[]> Rows(string path, int width) => File.ReadLines(path).Skip(1)
        .Where(line => line.Length > 0).Select(line =>
        {
            var fields = line.Split('\t');
            if (fields.Length != width || fields.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException($"Malformed row in {Path.GetFileName(path)}.");
            return fields;
        });

    private static Guid Id(string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes(name)).AsSpan(0, 16));

    // The caller supplies a scope fixed before loading the snapshot. This API only hands the
    // classifier detached evidence. It accepts neither filenames nor authority to read or write projects.
    public static Grounding? Resolve(RoomFixture room, Prompt prompt)
    {
        var selected = prompt.Selection == "-" ? null : room.Objects[prompt.Selection];
        var evidenceBytes = room.Snapshot;
        var scope = room.Scope;
        if (prompt.Category == "stale-scope")
            scope = scope with { Revision = scope.Revision + 1 };
        if (prompt.Category == "corrupt-evidence")
        {
            evidenceBytes = (byte[])evidenceBytes.Clone();
            evidenceBytes[^1] ^= 1;
        }
        return Resolve(scope, evidenceBytes, selected?.Id, prompt.Text);
    }

    public static Grounding? Resolve(ProjectReadScope authorized, ReadOnlySpan<byte> bytes, Guid? selected, string question)
    {
        if (question.Length > SceneQuestionHost.MaxQuestionLength)
            return null;
        SceneEvidence snapshot;
        string sha;
        try { (snapshot, sha) = SceneEvidenceProfile.Open(bytes); }
        catch (InvalidDataException) { return null; }
        if (snapshot.Scope != authorized)
            return null;

        var references = new List<(int Start, int Length, SceneSource? Source)>();
        foreach (var group in snapshot.Objects.GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            var expression = @"(?<![\p{L}\p{N}])" + Regex.Escape(group.Key) + @"(?![\p{L}\p{N}])";
            var source = group.Count() == 1 ? group.First() : null;
            references.AddRange(Regex.Matches(question, expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                .Select(match => (match.Index, match.Length, source)));
        }
        references.AddRange(Identifier().Matches(question).Select(match =>
            (match.Index, match.Length, snapshot.Objects.SingleOrDefault(item => item.Id == Guid.Parse(match.Value)))));
        references.AddRange(Selection().Matches(question).Select(match =>
            (match.Index, match.Length, snapshot.Objects.SingleOrDefault(item => item.Id == selected))));

        var spans = references.Where(span => !references.Any(other => other.Length > span.Length &&
            other.Start <= span.Start && other.Start + other.Length >= span.Start + span.Length)).ToArray();
        var candidates = spans.Select(span => span.Source).Distinct().ToArray();
        if (candidates is not [SceneSource sourceObject])
            return null;
        var text = new StringBuilder(question);
        foreach (var span in spans.Select(span => (span.Start, span.Length)).Distinct().OrderByDescending(span => span.Start))
            text.Remove(span.Start, span.Length).Insert(span.Start, " entityref ");
        var links = new[] { sourceObject.TargetId, sourceObject.SecondTargetId }.OfType<Guid>().ToArray();
        return new Grounding(sourceObject, text.ToString(), sha, links);
    }

    public static bool Supports(string label, SceneSourceKind kind) => label switch
    {
        "inspect" or "locate" => true,
        "link" => kind == SceneSourceKind.Switch,
        "state" => kind is SceneSourceKind.Lamp or SceneSourceKind.Door,
        _ => false
    };
}
