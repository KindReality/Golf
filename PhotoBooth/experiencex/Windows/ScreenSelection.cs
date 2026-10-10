namespace ExperienceX;

internal static class ScreenSelection
{
    // Selection always uses the configured coordinates, including before video starts.
    public static bool Contains(PointOptions k, double x, double y)
    {
        var points = k.Vertices;
        bool inside = false;
        for (int i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            if (Math.Abs((b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X)) < 1e-9 &&
                x >= Math.Min(a.X, b.X) - 1e-9 && x <= Math.Max(a.X, b.X) + 1e-9 && y >= Math.Min(a.Y, b.Y) - 1e-9 && y <= Math.Max(a.Y, b.Y) + 1e-9) return true;
            if ((a.Y > y) != (b.Y > y) && x < a.X + (y - a.Y) * (b.X - a.X) / (b.Y - a.Y)) inside = !inside;
        }
        return inside;
    }
    public static (string? Display, string? Name) Target(ExperienceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Screen))
            return (request.DisplayName, null);
        if (request.Screen.Trim() == "*")
            return (string.IsNullOrWhiteSpace(request.DisplayName) ? "*" : request.DisplayName.Trim(), "*");
        var split = request.Screen.LastIndexOf('/');
        if (split <= 0 || split == request.Screen.Length - 1)
            throw new ArgumentException("screen must be a combined display/screen name, such as Projector/screen1.");
        var display = request.Screen[..split].Trim();
        var name = request.Screen[(split + 1)..].Trim();
        if (name != "*") ScreenOptions.ValidateName(name);
        if (!string.IsNullOrWhiteSpace(request.DisplayName) && !display.Equals(request.DisplayName.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("displayName and the display in screen must match.");
        return (display, name);
    }
    public static IReadOnlyList<(string Display, ScreenOptions Screen)> VideoOutputs(ExperienceRequest request, ExperienceOptions options,
        IReadOnlyList<string> available, string? primary)
    {
        var target = Target(request);
        var single = target.Display == "*" ? null : OutputSelection.Display(target.Display, options.DefaultDisplayName, available, primary, options.DisplayAliases);
        var displays = target.Display == "*" ? available : single is null ? [] : new[] { single };
        var outputs = new List<(string, ScreenOptions)>();
        foreach (var display in displays.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var videos = options.ScreensFor(display).Where(s => !s.IsMat);
            if (target.Name is null) videos = videos.Take(1);
            else if (target.Name != "*") videos = videos.Where(s => s.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase));
            outputs.AddRange(videos.Select(screen => (display, screen)));
        }
        return outputs;
    }
}
