await TestSuites.RunAsync(args);

internal static class TestSuites
{
    public static async Task RunAsync(string[] arguments)
    {
        if (arguments.SequenceEqual(["--cursor"]))
        {
            IdleMouseCursorChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--keyboard"]))
        {
            await ConfigurationHotkeyChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--network"]))
        {
            await UdpListenerChecks.Run();
            await BroadcastChecks.Run();
            return;
        }
        if (arguments.Any(argument => argument is not ("--all" or "--hardware")))
            throw new ArgumentException("Usage: dotnet run --project ExperienceX.Tests -- [--all | --hardware | --keyboard | --network | --cursor]");
        bool hardware = arguments.Contains("--hardware") || arguments.Contains("--all");
        bool unit = !arguments.Contains("--hardware") || arguments.Contains("--all");
        if (unit)
        {
            CoreChecks.Run();
            IdleMouseCursorChecks.Run();
            MonitorBackgroundChecks.Run();
            await BackgroundPersistenceChecks.Run();
            AudioVolumeChecks.Run();
            CoordinatedAudioChecks.Run();
            await LocalConfigurationChecks.Run();
            await ConfigurationServiceChecks.Run();
            await ConfigurationHotkeyChecks.Run();
            await ApplicationLifetimeChecks.Run();
            SingleInstanceChecks.Run();
            await UdpListenerChecks.Run();
            await BroadcastChecks.Run();
            await CalibrationChecks.Run();
            ReliabilityChecks.Run();
            await CalibrationConfirmationChecks.Run();
            Console.WriteLine("All fast checks passed against the ExperienceX application assembly.");
        }
        if (hardware)
        {
            await CoordinatedAudioHardwareChecks.RunAsync();
            GpuShaderChecks.Run();
            await CalibrationVisibilityChecks.Run();
            await RendererRecoveryChecks.RunAsync();
            await WindowRecoveryChecks.Run();
            Console.WriteLine("All GPU and native-window checks passed.");
        }
    }
}
