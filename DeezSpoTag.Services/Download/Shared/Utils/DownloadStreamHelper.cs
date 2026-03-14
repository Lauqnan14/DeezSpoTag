namespace DeezSpoTag.Services.Download.Shared.Utils;

public static class DownloadStreamHelper
{
    public static async Task CopyToAsyncWithProgress(
        Stream input,
        Stream output,
        long? totalBytes,
        Func<double, double, Task>? progressCallback,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long readTotal = 0;
        var progressState = new ProgressState();

        if (progressCallback != null)
        {
            await progressCallback(0, 0);
        }

        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            readTotal += read;
            UpdateSpeed(progressState, read);
            await ReportProgressIfNeeded(progressCallback, totalBytes, readTotal, progressState);
        }

        if (progressCallback != null)
        {
            await progressCallback(100, 0);
        }
    }

    private sealed class ProgressState
    {
        public int LastPercent { get; set; } = -1;
        public DateTimeOffset LastReportAt { get; set; } = DateTimeOffset.UtcNow;
        public long SpeedWindowBytes { get; set; }
        public DateTimeOffset SpeedWindowStart { get; set; } = DateTimeOffset.UtcNow;
        public double CurrentSpeedMbps { get; set; }
    }

    private static void UpdateSpeed(ProgressState state, int read)
    {
        state.SpeedWindowBytes += read;
        var now = DateTimeOffset.UtcNow;
        var speedWindowSeconds = (now - state.SpeedWindowStart).TotalSeconds;
        if (speedWindowSeconds < 1)
        {
            return;
        }

        var bytesPerSecond = state.SpeedWindowBytes / speedWindowSeconds;
        state.CurrentSpeedMbps = (bytesPerSecond * 8) / 1024 / 1024;
        state.SpeedWindowBytes = 0;
        state.SpeedWindowStart = now;
    }

    private static async Task ReportProgressIfNeeded(
        Func<double, double, Task>? progressCallback,
        long? totalBytes,
        long readTotal,
        ProgressState state)
    {
        if (progressCallback == null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (totalBytes.HasValue && totalBytes.Value > 0)
        {
            var percent = (int)Math.Floor(readTotal * 100d / totalBytes.Value);
            if (percent != state.LastPercent || (now - state.LastReportAt).TotalSeconds >= 1)
            {
                state.LastPercent = percent;
                state.LastReportAt = now;
                await progressCallback(percent, state.CurrentSpeedMbps);
            }

            return;
        }

        if ((now - state.LastReportAt).TotalSeconds >= 1)
        {
            state.LastReportAt = now;
            await progressCallback(0, state.CurrentSpeedMbps);
        }
    }
}
