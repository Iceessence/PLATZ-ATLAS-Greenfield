namespace PlatzAtlas.Core;

public enum SceneObjectKind { Wall, Lamp, Door, Switch, Spawn, Key, Goal }

public static class SceneObjectFactory
{
    public static SceneEntity Create(Guid id, SceneObjectKind kind, string name, double x, double y)
    {
        var (component, width, height, rotation) = kind switch
        {
            SceneObjectKind.Wall => ((SceneComponent)new WallPanel(PanelColor.Slate), .36, .105, 0),
            SceneObjectKind.Lamp => (new SceneLamp(true), .10, .16, 0),
            SceneObjectKind.Door => (new SceneDoor(false), .18, .09, 0),
            SceneObjectKind.Switch => (new SceneSwitch(null, SwitchBehavior.Toggle), .045, .077, 45),
            SceneObjectKind.Spawn => (new SceneSpawn(), .065, .105, 0),
            SceneObjectKind.Key => (new SceneKey(), .056, .096, 0),
            SceneObjectKind.Goal => (new SceneGoal(0), .15, .15, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return new SceneEntity(id, name, new Transform2D(x, y, width, height, rotation), component);
    }
}
