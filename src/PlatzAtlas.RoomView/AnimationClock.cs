using System.Diagnostics;
using System.Windows.Threading;

namespace PlatzAtlas.RoomView;

public sealed class AnimationClock(Action draw)
{
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(32) };
    private long _started;
    private bool _subscribed;

    public double Milliseconds => _timer.IsEnabled ? Stopwatch.GetElapsedTime(_started).TotalMilliseconds : 0;

    public void SetActive(bool animated)
    {
        if (animated == _timer.IsEnabled)
            return;
        if (!animated)
        {
            _timer.Stop();
            return;
        }
        if (!_subscribed)
        {
            _timer.Tick += (_, _) => draw();
            _subscribed = true;
        }
        Restart();
        _timer.Start();
    }

    public void Restart() => _started = Stopwatch.GetTimestamp();
}
