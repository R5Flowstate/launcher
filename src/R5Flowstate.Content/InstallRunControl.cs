using System.Threading;

namespace R5Flowstate.Content;

/// <summary>Pause gate for an in-flight content install. Cancel is a separate CTS.</summary>
public sealed class InstallRunControl
{
    int _paused;

    public bool IsPaused => Volatile.Read(ref _paused) != 0;

    public void Pause() => Interlocked.Exchange(ref _paused, 1);

    public void Resume() => Interlocked.Exchange(ref _paused, 0);

    public async Task WaitIfPausedAsync(CancellationToken cancel)
    {
        while (Volatile.Read(ref _paused) != 0)
        {
            cancel.ThrowIfCancellationRequested();
            await Task.Delay(150, cancel).ConfigureAwait(false);
        }
    }
}
