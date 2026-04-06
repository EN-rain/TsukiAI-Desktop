using NAudio.Wave;
using System.IO;

namespace TsukiAI.VoiceChat.Services;

public sealed class TtsPlaybackService : IDisposable
{
    private readonly object _lock = new();
    private IWavePlayer? _player;
    private WaveStream? _reader;
    private MemoryStream? _stream;
    private TaskCompletionSource<bool>? _playbackTcs;

    public int OutputDeviceNumber { get; private set; } = -1;

    public void SetOutputDeviceNumber(int deviceNumber)
    {
        lock (_lock)
        {
            if (deviceNumber < -1 || deviceNumber >= WaveOut.DeviceCount)
                deviceNumber = -1;
            OutputDeviceNumber = deviceNumber;
        }
    }

    public static List<(int Id, string Name)> GetOutputDevices()
    {
        var list = new List<(int Id, string Name)> { (-1, "Default Audio Device") };
        for (int n = 0; n < WaveOut.DeviceCount; n++)
        {
            try
            {
                var caps = WaveOut.GetCapabilities(n);
                list.Add((n, caps.ProductName));
            }
            catch { }
        }

        return list;
    }

    public async Task PlayWavAsync(byte[] wavBytes, CancellationToken ct = default)
    {
        if (wavBytes is null || wavBytes.Length == 0) return;

        await Task.Run(async () =>
        {
            lock (_lock)
            {
                Stop_NoLock();
            }

            lock (_lock)
            {
                _stream = new MemoryStream(wavBytes, writable: false);
                _reader = new WaveFileReader(_stream);
                var waveOut = new WaveOutEvent { DeviceNumber = OutputDeviceNumber };
                _playbackTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                waveOut.PlaybackStopped += (_, _) =>
                {
                    lock (_lock)
                    {
                        _playbackTcs?.TrySetResult(true);
                    }
                };
                waveOut.Init(_reader);
                _player = waveOut;
                _player.Play();
            }
        }, ct);

        Task? task;
        lock (_lock) task = _playbackTcs?.Task;
        if (task is not null)
            await WaitWithCancellationAsync(task, ct);
    }

    public void Stop()
    {
        lock (_lock) Stop_NoLock();
    }

    private void Stop_NoLock()
    {
        try { _player?.Stop(); } catch { }
        try { _player?.Dispose(); } catch { }
        _player = null;

        try { _reader?.Dispose(); } catch { }
        _reader = null;
        try { _stream?.Dispose(); } catch { }
        _stream = null;

        try { _playbackTcs?.TrySetCanceled(); } catch { }
        _playbackTcs = null;
    }

    private static async Task WaitWithCancellationAsync(Task task, CancellationToken ct)
    {
        if (task.IsCompleted) { await task; return; }
        var cancelTask = Task.Delay(Timeout.Infinite, ct);
        var done = await Task.WhenAny(task, cancelTask);
        if (done == cancelTask)
            ct.ThrowIfCancellationRequested();
        await task;
    }

    public void Dispose()
    {
        Stop();
    }
}
