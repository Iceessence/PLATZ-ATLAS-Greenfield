using System.Windows.Input;
using PlatzAtlas.Runtime;

namespace PlatzAtlas.RoomView;

public static class WalkInput
{
    public static WalkDirection? Direction(Key key) => key switch
    {
        Key.Left or Key.A => WalkDirection.Left,
        Key.Right or Key.D => WalkDirection.Right,
        Key.Up or Key.W => WalkDirection.Up,
        Key.Down or Key.S => WalkDirection.Down,
        _ => null
    };
}
