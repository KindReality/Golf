using System.Text.Json;
using Controller.Configuration;
using Controller.Diagnostics;
using PhotoBooth.Media;

internal static class PlaylistMediaValidationChecks
{
    internal static void Run()
    {
        PlaylistCommand Play(string experience, string value) => new() { Device = "*", Payload = JsonSerializer.SerializeToElement(new { experience, value }) };
        var options = new ControllerOptions { Playlists = [new() { Name = "show", Items = [new() { Name = "cue", Enabled = false, Commands =
            [Play("video", "ghosts\\Eye.mp4"), Play("audio", "Missing.wav"), Play("video", "Missing.mp4"), Play("video", "Missing.mp4"),
             new() { Playlist = "child" }, new() { Device = "*", Payload = JsonSerializer.SerializeToElement(new { command = "scene", value = "active" }) },
             new() { Device = "*", Payload = JsonSerializer.SerializeToElement(new { experience = "video", command = "stop", value = "NotMedia.mp4" }) }] }] }] };
        var inventory = new MediaInventory { Files = [new("Ghosts/Eye.mp4", 1, DateTime.UtcNow)] };
        var log = new ActivityLog();
        PlaylistMediaValidation.WarnMissing(options, inventory, log);
        var warnings = log.Snapshot();
        if (warnings.Length != 2 || warnings.Any(e => e.Level != "Warning" || !e.Message.Contains("show/cue")) ||
            !warnings.Any(e => e.Message.Contains("Missing.wav")) || !warnings.Any(e => e.Message.Contains("Missing.mp4")))
            throw new Exception("Startup media warnings must identify missing audio/video references once, without treating scene/stop/nested commands as files.");
        Console.WriteLine("PASS Startup warnings identify missing playlist media, including disabled references, without blocking configuration or playback.");
    }
}
