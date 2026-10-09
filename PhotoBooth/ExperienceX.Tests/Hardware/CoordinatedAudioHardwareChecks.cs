using ExperienceX;
using NAudio.Wave;

internal static class CoordinatedAudioHardwareChecks
{
    public static async Task RunAsync()
    {
        var devices = AudioManager.ReadInventory().Devices.Take(3).ToArray();
        if (devices.Length < 2)
            throw new Exception("Two audio endpoints are required for coordinated audio hardware checks.");
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        try
        {
            using (var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)))
                writer.WriteSamples(new float[48000 * 2 * 4], 0, 48000 * 2 * 4);
            var options = new ExperienceOptions { MaxAudioPlaybackLength = 1.5, AudioTransitionInSeconds = 0.1, AudioTransitionOutSeconds = 0.2 };
            var request = new ExperienceRequest { Experience = "audio", Value = path, Mute = true, RequestId = "hardware-audio-" + Guid.NewGuid() };
            // Cancellation after preparation but before starting must release every native client.
            using (var prepared = new CoordinatedAudioPlayback(devices, path, request, options))
            {
                await prepared.Prepared.WaitAsync(TimeSpan.FromSeconds(10));
                if (prepared.ActiveOutputCount != devices.Length)
                    throw new Exception("Not every endpoint was prepared.");
            }
            // Feed an inventory removal without changing the user's actual Windows devices.
            using (var playing = new CoordinatedAudioPlayback(devices, path, request, options))
            {
                await playing.Prepared.WaitAsync(TimeSpan.FromSeconds(10));
                var completed = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                playing.Start((_, error) => completed.TrySetResult(error));
                await Task.Delay(600);
                playing.CheckDevices(devices.Skip(1).Select(device => device.Id).ToHashSet(StringComparer.OrdinalIgnoreCase));
                await Task.Delay(150);
                if (playing.ActiveOutputCount != devices.Length - 1)
                    throw new Exception("Inventory removal did not preserve the other outputs.");
                if (await completed.Task.WaitAsync(TimeSpan.FromSeconds(10)) is Exception error)
                    throw error;
                if (playing.ActiveOutputCount != 0)
                    throw new Exception("Completed group retained native outputs.");
            }
            // Shutdown while playing must wake the render wait and finish the worker.
            using var active = new CoordinatedAudioPlayback(devices, path, request, options);
            await active.Prepared.WaitAsync(TimeSpan.FromSeconds(10));
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            active.Start((_, _) => stopped.TrySetResult());
            await Task.Delay(500);
            active.Dispose();
            if (!stopped.Task.IsCompleted || active.ActiveOutputCount != 0)
                throw new Exception("Group shutdown did not drain native resources.");
            Console.WriteLine("Passed coordinated audio native preparation cancellation, endpoint removal isolation, natural cleanup and active shutdown.");
        }
        finally { File.Delete(path); }
    }
}
