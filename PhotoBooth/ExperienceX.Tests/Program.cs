await TestSuites.RunAsync(args);

internal static class TestSuites
{
    public static async Task RunAsync(string[] arguments)
    {
        if (arguments.SequenceEqual(["--sizing"]))
        {
            await ScreenSizingChecks.Run();
            await ConfigurationHotkeyChecks.Run();
            GpuShaderChecks.Run();
            await ScreenEditingWorkflowChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--updates"]))
        {
            await BuildUpdateChecks.Run("ExperienceX");
            return;
        }
        if (arguments.SequenceEqual(["--context"]))
        {
            await CommandContextChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--configuration"]))
        {
            await MachineConfigurationChecks.Run();
            await ConfigurationServiceChecks.Run();
            await BackgroundPersistenceChecks.Run();
            await ScreenChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--all-screen-playback"]))
        {
            await AllScreenPlaybackChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--video-loop"]))
        {
            VideoLoopChecks.Run();
            await VideoLoopHardwareChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--media-discovery"]))
        {
            await MediaPathChecks.Run();
            await MediaDiscoveryChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--desktop-transparency"]))
        {
            await DesktopTransparencyChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--configuration-visibility"]))
        {
            await CalibrationVisibilityChecks.Run();
            await DesktopTransparencyChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--screen-transparency"]))
        {
            await ScreenTransparencyChecks.Run();
            await ScreenSizingChecks.Run();
            await MediaDiscoveryChecks.Run();
            await ScreenChromaKeyHardwareChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--screen-workflow"]))
        {
            await ScreenEditingWorkflowChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--mats"]))
        {
            await MatChecks.Run();
            await MatHardwareChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--cursor"]))
        {
            IdleMouseCursorChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--screens"]))
        {
            await ScreenChecks.Run();
            await MatChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--screens-hardware"]))
        {
            GpuShaderChecks.Run();
            await ScreenChromaKeyHardwareChecks.Run();
            await MatHardwareChecks.Run();
            await CalibrationVisibilityChecks.Run();
            await MultiScreenHardwareChecks.Run();
            await RendererRecoveryChecks.RunAsync();
            await WindowRecoveryChecks.Run();
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
            await MediaPathChecks.Run();
            CoreChecks.Run();
            VideoLoopChecks.Run();
            await ScreenChecks.Run();
            await MatChecks.Run();
            IdleMouseCursorChecks.Run();
            DisplayBackgroundChecks.Run();
            await BackgroundPersistenceChecks.Run();
            BackgroundSelectionChecks.Run();
            await ScreenTransparencyChecks.Run();
            await ScreenSizingChecks.Run();
            AudioVolumeChecks.Run();
            CoordinatedAudioChecks.Run();
            await MachineConfigurationChecks.Run();
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
            await ScreenEditingWorkflowChecks.Run();
            await CoordinatedAudioHardwareChecks.RunAsync();
            GpuShaderChecks.Run();
            await ScreenChromaKeyHardwareChecks.Run();
            await MatHardwareChecks.Run();
            await MultiScreenHardwareChecks.Run();
            await CalibrationVisibilityChecks.Run();
            await RendererRecoveryChecks.RunAsync();
            await WindowRecoveryChecks.Run();
            Console.WriteLine("All GPU and native-window checks passed.");
        }
    }
}
