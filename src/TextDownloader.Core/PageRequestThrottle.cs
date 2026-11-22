using System.Diagnostics;

namespace TextDownloader.Core;

internal sealed class PageRequestThrottle
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long? _lastRequest;

    public async Task EnterAsync(TimeSpan interval, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_lastRequest is { } last)
            {
                var remaining = interval - Stopwatch.GetElapsedTime(last);
                while (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(
                        Math.Ceiling(remaining.TotalMilliseconds), 1, 60_000)), token).ConfigureAwait(false);
                    remaining = interval - Stopwatch.GetElapsedTime(last);
                }
            }
            token.ThrowIfCancellationRequested();
            _lastRequest = Stopwatch.GetTimestamp();
        }
        catch
        {
            Exit();
            throw;
        }
    }

    public void Exit() => _gate.Release();
}
