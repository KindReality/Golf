using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using Controller.Application;
using Controller.Configuration;
using Controller.Diagnostics;
using Controller.Networking;
using Controller.Windows;
using PhotoBooth.Networking;

internal static class RoleCatalogChecks
{
    internal static async Task Run()
    {
        var item = new PlaylistItem { Name = "disabled", Enabled = false, Roles = "^Subwoofer,*,Front", Commands = [new() { Playlist = "unused" }] };
        var options = new ControllerOptions { ControllerId = Guid.NewGuid().ToString("N"), DeviceName = "roles-controller", DefinitionsPort = FreePort(),
            Scenes = [new() { Name = "idle", AmbientPlaylist = "ambient" }], Playlists = [new() { Name = "ambient", Type = "ambient", Items = [item] },
                new() { Name = "unused", Items = [new() { Name = "standalone", Roles = "Unassigned,front", Commands = [new() { Device = "*", Payload = JsonSerializer.SerializeToElement(new { experience = "video", value = "Eye.mp4", roles = "^CommandOnly,*" }) }] }] }] };
        var names = PlaylistRoles.Names(options.Playlists);
        Check(names.SequenceEqual(new[] { "CommandOnly", "Front", "Subwoofer", "Unassigned" }, StringComparer.OrdinalIgnoreCase),
            "Role suggestions include all playlists, nested references, disabled items and direct command roles, with exclusions unwrapped and wildcards omitted.");
        var inventory = new ExperienceInventory { DeviceName = "peer", Hostname = Environment.MachineName, Screens = [new() { Roles = "Front", Available = true }], Speakers = [new() { Roles = "Speaker" }] };
        ExperiencePeer[] peers = [new(inventory, true, false)];
        var live = LivePlayback.Roles(peers, names);
        Check(live.Single(r => r.Name == "Unassigned") is { Screens: 0, Speakers: 0 } && live.Single(r => r.Name == "Front").Screens == 1,
            "Live retains predetermined roles with no outputs while counting eligible devices normally.");
        using (var editor = new PlaylistEditorView(options.Playlists[0], ShowConfiguration.From(options), options, peers, [], [], () => { }, roles: ["PeerRole"]))
        {
            var field = Descendants(editor).OfType<ComboBox>().Single(c => c.AccessibleName == "Roles (comma-separated)");
            Check(field.Text == item.Roles && field.Items.Cast<string>().Contains("Unassigned") && field.Items.Cast<string>().Contains("PeerRole"),
                "Playlist role choices combine shared and draft roles without changing ordered rules.");
        }
        // Native controls install a synchronization context; following awaits must not capture it.
        var previous = Environment.GetEnvironmentVariable(LocalTestNetwork.Variable);
        Environment.SetEnvironmentVariable(LocalTestNetwork.Variable, "127.0.0.2");
        try
        {
            string[] assigned = ["AssignedOnly"]; byte[] announcement = [];
            var peer = new ControllerSharedDefinitions { ControllerId = Guid.NewGuid().ToString("N"), ControllerName = "remote", Hostname = "OTHERPC", Roles = ["PeerRole"] };
            await using var server = new ControllerAnnouncements(() => options, new ActivityLog(), (bytes, _, _) => { announcement = bytes; return Task.CompletedTask; }, peers: () => [peer], assignedRoles: () => assigned);
            server.Publish(); await server.SendAsync().ConfigureAwait(false);
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            async Task<byte[]> Read(string route) => await http.GetByteArrayAsync($"http://127.0.0.2:{server.Port}/{route}").ConfigureAwait(false);
            var bytes = await Read("definitions").ConfigureAwait(false);
            var own = JsonSerializer.Deserialize<ControllerSharedDefinitions>(bytes, ControllerSharedDefinitions.Json)!; own.Validate();
            Check(own.Roles.Contains("AssignedOnly") && own.Roles.Contains("Unassigned") && !own.Roles.Contains("PeerRole") &&
                ControllerDiscovery.ReadAnnouncement(BroadcastEnvelope.Parse(announcement))!.Revision == ControllerSharedDefinitions.Revision(bytes),
                "Controller HTTP definitions publish its playlist and assigned roles with the matching discovery revision, without relaying peer definitions.");
            var catalog = JsonSerializer.Deserialize<ControllerCatalog>(await Read("catalog").ConfigureAwait(false), ControllerSharedDefinitions.Json)!; catalog.Validate();
            Check(catalog.Controllers.SelectMany(c => c.Roles).Contains("PeerRole"), "The local catalog includes roles from remote Controllers.");
            assigned = ["NewAssigned"]; await server.SendAsync().ConfigureAwait(false);
            var changed = await Read("definitions").ConfigureAwait(false);
            Check(ControllerSharedDefinitions.Revision(changed) != ControllerSharedDefinitions.Revision(bytes) &&
                JsonSerializer.Deserialize<ControllerSharedDefinitions>(changed, ControllerSharedDefinitions.Json)!.Roles.Contains("NewAssigned"),
                "Changed assignments refresh definitions on the normal announcement path.");
            options = options with { Playlists = [] }; assigned = []; await server.SendAsync().ConfigureAwait(false);
            Check(JsonSerializer.Deserialize<ControllerSharedDefinitions>(await Read("definitions").ConfigureAwait(false), ControllerSharedDefinitions.Json)!.Roles.Length == 0,
                "Removing the source roles removes their suggestions without creating permanent role configuration.");
        }
        finally { Environment.SetEnvironmentVariable(LocalTestNetwork.Variable, previous); }
    }
    private static int FreePort() { using var socket = new TcpListener(IPAddress.Loopback, 0); socket.Start(); return ((IPEndPoint)socket.LocalEndpoint).Port; }
    private static IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
}
