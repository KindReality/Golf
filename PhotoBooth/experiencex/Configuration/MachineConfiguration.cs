using PhotoBooth.Configuration;

namespace ExperienceX;

internal static class MachineConfiguration
{
    public static string Resolve(string? oneDriveRoot = null, string? hostname = null) =>
        MachineConfigurationFiles.Resolve("ExperienceX", oneDriveRoot, hostname);

    public static string? Initialize(string path, string templateDirectory) =>
        MachineConfigurationFiles.Initialize(path, templateDirectory, json => ExperienceOptions.Parse(json));
}
