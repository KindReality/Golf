using PhotoBooth.Networking;

namespace Controller.Configuration;

internal static class PlaylistRoles
{
    // Scanning the whole collection includes nested, disabled and unreferenced playlists.
    internal static string[] Names(IEnumerable<PlaylistOptions> playlists) => RoleCatalog.Names(playlists.SelectMany(p => p.Items)
        .SelectMany(item => new[] { item.Roles }.Concat(item.Commands.Select(command => ControllerOptions.Text(command.Payload, "roles")))));
}
