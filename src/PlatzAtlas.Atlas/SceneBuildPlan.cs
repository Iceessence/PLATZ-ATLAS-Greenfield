using System.Text.RegularExpressions;

namespace PlatzAtlas.Atlas;

public sealed partial record SceneBuildPlan(string Name, string Lane)
{
    public const int Version = 1;

    [GeneratedRegex(@"^Build a keyed gate named (?<name>[\p{L}\p{N}][\p{L}\p{N} _-]{0,47}) on the (?<lane>left|center|right)\.?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GateRequest();

    internal static SceneBuildPlan? Parse(string request)
    {
        var match = GateRequest().Match(request.Trim());
        return match.Success ? new SceneBuildPlan(match.Groups["name"].Value.Trim(), match.Groups["lane"].Value.ToLowerInvariant()) : null;
    }

    public string Summary => $"Build the {Name} keyed gate on the {Lane} with a closed door, linked switch, one collectible key and a key-gated exit.";
}
