using NAudio.Wave;

namespace Beacon.Voice;

public enum RecordingEnd
{
    /// <summary>Speech was heard and then the speaker stopped.</summary>
    SilenceAfterSpeech,

    /// <summary>The user pressed the mic button (or the shortcut) again.</summary>
    Stopped,

    /// <summary>Nothing above the noise floor within the grace period.</summary>
    NoSpeech,

    /// <summary>Hit the length cap.</summary>
    MaxLength,
}

public sealed record Recording(float[] Samples, RecordingEnd End);

/// <summary>
/// Captures one utterance from the microphone as 16 kHz mono float samples - the format
/// Whisper expects, so no resampling pass is needed afterwards.
///
/// Ends itself on silence: a launcher query is a short phrase, and making the user click
/// again to stop would double the interaction. The detector is deliberately simple - the
/// noise floor is learned from the first few hundred milliseconds, and speech is anything
/// comfortably above it.
/// </summary>
public sealed class MicRecorder
{
    public const int SampleRate = 16_000;

    private static readonly TimeSpan CalibrationWindow = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan NoSpeechTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan EndOfSpeechSilence = TimeSpan.FromMilliseconds(1100);
    private static readonly TimeSpan MaxLength = TimeSpan.FromSeconds(30);

    /// <summary>Raised roughly every 50 ms with a 0-1 input level, for the UI meter.</summary>
    public event Action<double>? Level;

    public static IReadOnlyList<string> Devices()
    {
        var names = new List<string>();
        for (var i = 0; i < WaveIn.DeviceCount; i++)
            names.Add(WaveIn.GetCapabilities(i).ProductName);
        return names;
    }

    public Task<Recording> RecordAsync(int deviceNumber, bool autoStop, CancellationToken stop)
    {
        var tcs = new TaskCompletionSource<Recording>(TaskCreationOptions.RunContinuationsAsynchronously);
        var samples = new List<float>(SampleRate * 8);

        var waveIn = new WaveIn
        {
            DeviceNumber = deviceNumber >= 0 && deviceNumber < WaveIn.DeviceCount ? deviceNumber : 0,
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 50,
        };

        double noiseFloor = 0;
        var calibrationSamples = 0;
        var heardSpeech = false;
        long lastSpeechSample = 0;
        var end = RecordingEnd.Stopped;
        var finished = 0;

        void Finish(RecordingEnd reason)
        {
            if (Interlocked.Exchange(ref finished, 1) != 0) return;
            end = reason;
            try
            {
                waveIn.StopRecording();
            }
            catch
            {
                // Already stopping.
            }
        }

        waveIn.DataAvailable += (_, e) =>
        {
            var count = e.BytesRecorded / 2;
            double sumSquares = 0;

            for (var i = 0; i < count; i++)
            {
                var sample = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
                samples.Add(sample);
                sumSquares += sample * sample;
            }

            if (count == 0) return;

            var rms = Math.Sqrt(sumSquares / count);
            Level?.Invoke(Math.Clamp(rms * 12, 0, 1));

            var recorded = samples.Count;

            // Learn what "quiet" sounds like before judging anything as speech.
            if (calibrationSamples < CalibrationWindow.TotalSeconds * SampleRate)
            {
                noiseFloor = (noiseFloor * calibrationSamples + rms * count) / (calibrationSamples + count);
                calibrationSamples += count;
                return;
            }

            var threshold = Math.Max(0.012, noiseFloor * 3.0);
            if (rms > threshold)
            {
                heardSpeech = true;
                lastSpeechSample = recorded;
            }

            if (recorded >= MaxLength.TotalSeconds * SampleRate)
            {
                Finish(RecordingEnd.MaxLength);
                return;
            }

            if (!autoStop) return;

            if (!heardSpeech && recorded >= NoSpeechTimeout.TotalSeconds * SampleRate)
                Finish(RecordingEnd.NoSpeech);
            else if (heardSpeech && recorded - lastSpeechSample >= EndOfSpeechSilence.TotalSeconds * SampleRate)
                Finish(RecordingEnd.SilenceAfterSpeech);
        };

        waveIn.RecordingStopped += (_, e) =>
        {
            waveIn.Dispose();

            if (e.Exception is not null)
            {
                tcs.TrySetException(e.Exception);
                return;
            }

            // A manual stop before any speech is still "nothing heard".
            var reason = !heardSpeech && end == RecordingEnd.Stopped ? RecordingEnd.NoSpeech : end;
            tcs.TrySetResult(new Recording(samples.ToArray(), reason));
        };

        stop.Register(() => Finish(RecordingEnd.Stopped));

        try
        {
            waveIn.StartRecording();
        }
        catch (Exception ex)
        {
            waveIn.Dispose();
            tcs.TrySetException(ex);
        }

        return tcs.Task;
    }
}
