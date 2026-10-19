await TestSuites.RunAsync(args);

internal static class TestSuites
{
    public static async Task RunAsync(string[] arguments)
    {
        if (arguments.SequenceEqual(["--priority-events"])) { VideoPriorityChecks.Run(); await PlaybackPriorityEventChecks.Run(); return; }
        if (arguments.SequenceEqual(["--display-context"])) { await DisplayContextChecks.Run(); await DisplayContextHardwareChecks.Run(); return; }
        if (arguments.SequenceEqual(["--calibration-performance"])) { await CalibrationOptimizationChecks.Run(); await CalibrationVisibilityChecks.Run(); return; }
        if (arguments.SequenceEqual(["--renderer-safety"])) { RendererSafetyChecks.Run(); await RendererSafetyHardwareChecks.Run(); return; }
        if (arguments.SequenceEqual(["--renderer-soak"])) { await RendererEditingSoakChecks.Run(); return; }
        if (arguments.SequenceEqual(["--software-video"])) { await ConfigurationServiceChecks.Run(); await SoftwareVideoHardwareChecks.Run(); return; }
        if (arguments.SequenceEqual(["--software-video-loop"])) { VideoLoopChecks.Run(); await VideoLoopHardwareChecks.Run(softwareVideoDecoding: true); return; }
        if (arguments.SequenceEqual(["--software-video-soak"])) { await RendererEditingSoakChecks.Run(softwareVideoDecoding: true); return; }
        if (arguments.SequenceEqual(["--renderer-diagnostics"])) { RendererProgressChecks.Run(); await RendererDiagnosticsChecks.Run(); return; }
        if (arguments.SequenceEqual(["--renderer-capture"])) { await RendererCaptureChecks.Run(); return; }
        if (arguments.SequenceEqual(["--taskbar"])) { await TaskbarChecks.Run(); return; }
        if (arguments.SequenceEqual(["--video-outline"]))
        {
            await VideoOutlineChecks.Run(); await VideoOutlineHardwareChecks.Run();
            await MatChecks.Run(); await MatHardwareChecks.Run(); await VideoFramingChecks.Run(); return;
        }
        if (arguments.SequenceEqual(["--submenus"]))
        {
            await ConfigurationHotkeyChecks.Run(); BackgroundSelectionChecks.Run();
            await CalibrationVisibilityChecks.Run(); await ConfigurationSubmenuChecks.Run(); return;
        }
        if (arguments.SequenceEqual(["--restart"])) { await ExperienceRestartChecks.Run(); return; }
        if (arguments.SequenceEqual(["--lifecycle"])) { await LocalControllerExperienceChecks.Run(lifecycleOnly:true); return; }
        if (arguments.SequenceEqual(["--point-joins"]))
        {
            await PointSnappingChecks.Run(); await ScreenChecks.Run(); await MatChecks.Run();
            await LedChecks.Run(); GpuShaderChecks.Run(); await CalibrationVisibilityChecks.Run();
            await PointSnappingHardwareChecks.Run(); return;
        }
        if (arguments.SequenceEqual(["--screen-copy"])) { await ScreenCopyChecks.Run(); await ConfigurationHotkeyChecks.Run(); await PointSnappingChecks.Run(); await ScreenCopyHardwareChecks.Run(); return; }
        if (arguments.SequenceEqual(["--video-framing"])) { await VideoResetChecks.Run(); await ConfigurationHotkeyChecks.Run(); await VideoFramingChecks.Run(); await ModifierFramingChecks.Run(); GpuShaderChecks.Run(); await ZoomRotationGpuChecks.Run(); await CalibrationVisibilityChecks.Run(); await VideoFramingHardwareChecks.Run(); return; }
        if (arguments.SequenceEqual(["--mask-edges"])) { await VisualizationMaskEdgeChecks.Run(); await VisualizationConfigurationChecks.Run(); await MatHardwareChecks.Run(); return; }
        if (arguments.SequenceEqual(["--screen-layers"])) { await ScreenLayerChecks.Run(); await MatHardwareChecks.Run(); return; }
        if (arguments.SequenceEqual(["--roles"]))
        {
            await RoleConfigurationChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--roles-hardware"]))
        {
            await RolePlaybackHardwareChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--video-priority"]))
        {
            VideoPriorityChecks.Run();
            await VideoPriorityHardwareChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--audio"]))
        {
            AudioVolumeChecks.Run();
            CoordinatedAudioChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--audio-hardware"]))
        {
            await CoordinatedAudioHardwareChecks.RunAsync();
            return;
        }
        if(arguments.SequenceEqual(["--led-unit"])) { await LedChecks.Run(); return; }
        if(arguments.SequenceEqual(["--led"])) { await LedChecks.Run(); await LedHardwareChecks.Run(); await LedEditingChecks.Run(); return; }
        if(arguments.SequenceEqual(["--led-editing"])) { await LedEditingChecks.Run(); return; }
        if (arguments.SequenceEqual(["--gforce-control"]))
        {
            await VisualizationCommandChecks.Run();
            await GForceControlChecks.Run();
            await GForceControlHardwareChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--gforce-control-protocol"]))
        {
            await GForceControlChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--snapping-hardware"]))
        {
            await PointSnappingHardwareChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--snapping"]))
        {
            await PointSnappingChecks.Run();
            await CalibrationChecks.Run();
            await CalibrationConfirmationChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--visualization"]))
        {
            await VisualizationConfigurationChecks.Run();
            await VisualizationChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--gforce-native-fullscreen"]))
        {
            await GForceFullscreenChecks.Run(true,true);
            return;
        }
        if (arguments.SequenceEqual(["--gforce-fullscreen-key"]))
        {
            await GForceFullscreenChecks.Run(true);
            return;
        }
        if (arguments.SequenceEqual(["--gforce-fullscreen"]))
        {
            await GForceFullscreenChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--shortcuts"])) { await ConfigurationHotkeyChecks.Run(); return; }
        if (arguments.SequenceEqual(["--live-integration"])) { await LocalControllerExperienceChecks.Run(liveOnly:true); return; }
        if (arguments.SequenceEqual(["--local-focus"]))
        {
            await LocalControllerExperienceChecks.Run(true);
            return;
        }
        if (arguments.SequenceEqual(["--local-integration"]))
        {
            await LocalControllerExperienceChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--perspectives"]))
        {
            await PerspectiveChecks.Run();
            await ConfigurationHotkeyChecks.Run();
            GpuShaderChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--brightness"]))
        {
            await ScreenBrightnessChecks.Run();
            await ConfigurationHotkeyChecks.Run();
            GpuShaderChecks.Run();
            await ScreenMediaBrowsingHardwareChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--name-browsing"]))
        {
            await ScreenMediaBrowserChecks.Run();
            await ConfigurationHotkeyChecks.Run();
            await ScreenMediaBrowsingHardwareChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--screen-media"]))
        {
            ScreenMediaBindingChecks.Run();
            GpuShaderChecks.Run();
            await ScreenMediaBindingHardwareChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--perspective-create-hardware"])) { await PerspectiveCreationHardwareChecks.Run(); return; }
        if (arguments.SequenceEqual(["--perspective-crop"]))
        {
            await ScreenPerspectiveCropChecks.Run();
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
        if (arguments.SequenceEqual(["--background-visualization"])) { BackgroundSelectionChecks.Run(); await BackgroundPersistenceChecks.Run(); await BackgroundVisualizationChecks.Run(); return; }
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
            await ScreenPerspectiveCropChecks.Run();
            await MediaDiscoveryChecks.Run();
            await ScreenChromaKeyHardwareChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--screen-workflow"]))
        {
            await ScreenEditingWorkflowChecks.Run();
            return;
        }
        if (arguments.SequenceEqual(["--screen-dialog"]))
        {
            await VideoScreenDialogChecks.Run();
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
            await VideoOutlineChecks.Run();
            await ScreenCopyChecks.Run();
            await VideoFramingChecks.Run();
            await ModifierFramingChecks.Run();
            await LedChecks.Run();
            await VisualizationCommandChecks.Run();
            await GForceControlChecks.Run();
            await VisualizationConfigurationChecks.Run();
            await PerspectiveChecks.Run();
            ScreenMediaBindingChecks.Run();
            await ScreenMediaBrowserChecks.Run();
            await MediaPathChecks.Run();
            CoreChecks.Run();
            VideoLoopChecks.Run();
            await ScreenChecks.Run();
            VideoPriorityChecks.Run();
            await DisplayContextChecks.Run();
            await MatChecks.Run();
            IdleMouseCursorChecks.Run();
            DisplayBackgroundChecks.Run();
            await BackgroundPersistenceChecks.Run();
            BackgroundSelectionChecks.Run();
            await ScreenTransparencyChecks.Run();
            await ScreenPerspectiveCropChecks.Run();
            AudioVolumeChecks.Run();
            await ScreenBrightnessChecks.Run();
            CoordinatedAudioChecks.Run();
            await MachineConfigurationChecks.Run();
            await ConfigurationServiceChecks.Run();
            await ConfigurationHotkeyChecks.Run();
            await ApplicationLifetimeChecks.Run();
            SingleInstanceChecks.Run();
            await UdpListenerChecks.Run();
            await BroadcastChecks.Run();
            await PointSnappingChecks.Run();
            await CalibrationChecks.Run();
            ReliabilityChecks.Run();
            await CalibrationConfirmationChecks.Run();
            Console.WriteLine("All fast checks passed against the ExperienceX application assembly.");
        }
        if (hardware)
        {
            await DisplayContextHardwareChecks.Run();
            await VideoOutlineHardwareChecks.Run();
            await ScreenCopyHardwareChecks.Run();
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
