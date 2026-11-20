using System.Text.Json.Serialization;

namespace PlatzAtlas.Core;

public sealed record SceneAppearance(string? Title, uint? BackgroundRgb, IReadOnlyList<Guid>? PaintOrder)
{
    public const uint DefaultBackgroundRgb = 0x111921;

    public static IEnumerable<SceneEntity> Ordered(IReadOnlyList<SceneEntity> entities, IReadOnlyList<Guid>? order = null)
    {
        var baseline = entities.OrderBy(item => item.Component is WallPanel ? 0 : item.Component is SceneSprite ? 2 : 1)
            .ThenBy(item => item.Id);
        if (order is null)
            return baseline;
        var members = entities.ToDictionary(item => item.Id);
        return order.Where(members.ContainsKey).Select(id => members[id])
            .Concat(baseline.Where(item => !order.Contains(item.Id)));
    }

    internal static bool Same(SceneAppearance? first, SceneAppearance? second) =>
        first?.Title == second?.Title && first?.BackgroundRgb == second?.BackgroundRgb &&
        (first?.PaintOrder ?? []).SequenceEqual(second?.PaintOrder ?? []);

    internal static SceneAppearance? Freeze(SceneAppearance? style, IReadOnlySet<Guid>? knownEntities = null)
    {
        if (style is null)
            return null;
        if ((style.Title is { } title && (string.IsNullOrWhiteSpace(title) || title.Length > 96 || title.Any(char.IsControl))) ||
            style.BackgroundRgb is > 0xffffff)
            throw new InvalidDataException("Use a printable room title and a six-digit floor color.");
        if (style.PaintOrder is { } order && (order.Count > SceneValidation.MaxEntities || order.Distinct().Count() != order.Count ||
            order.Any(id => id == Guid.Empty || (knownEntities is not null && !knownEntities.Contains(id)))))
            throw new InvalidDataException("The saved paint order has unknown or duplicated objects.");
        if (style is { Title: null, BackgroundRgb: null } && (style.PaintOrder?.Count ?? 0) == 0)
            return null;
        return style with { PaintOrder = style.PaintOrder is { Count: > 0 } ids ? Array.AsReadOnly(ids.ToArray()) : null };
    }
}

public sealed record ConfigureRoomAppearance(Guid ProjectId, long BaseRevision, Guid EntityId, string Title, uint BackgroundRgb)
    : SceneOperation(ProjectId, BaseRevision, EntityId);
public sealed record ShiftSceneLayer(Guid ProjectId, long BaseRevision, Guid EntityId, bool Forward)
    : SceneOperation(ProjectId, BaseRevision, EntityId);
public sealed record RoomAppearanceChange(SceneAppearance? Before, SceneAppearance? After)
{
    internal RoomAppearanceChange Reverse() => new(After, Before);
    internal bool Inverts(RoomAppearanceChange candidate) =>
        SceneAppearance.Same(Before, candidate.After) && SceneAppearance.Same(After, candidate.Before);
}
