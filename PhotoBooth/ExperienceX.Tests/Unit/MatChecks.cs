using System.Text.Json;
using System.Text.Json.Nodes;
using ExperienceX;

internal static class MatChecks
{
    public static async Task Run()
    {
        static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        var concave = new PointOptions { Enabled = true, Vertices = [new(.1, .1), new(.9, .1), new(.9, .9), new(.5, .5), new(.1, .9)] };
        concave.ValidatePolygon();
        Check(ScreenSelection.Contains(concave, .5, .3) && !ScreenSelection.Contains(concave, .5, .8), "Concave mat selection filled its cutout.");
        var triangles = PolygonGeometry.Triangles(concave);
        double area = 0;
        for (int i = 0; i < triangles.Length; i += 3) area += PolygonGeometry.Cross(triangles[i], triangles[i + 1], triangles[i + 2]) / 2;
        Check(Math.Abs(area - .48) < 1e-9, "Mat triangulation filled a concavity or left a hole.");
        var edge = PolygonGeometry.ClosestEdge(concave, .5, .104, 1920, 1080);
        Check(edge.Edge == 0 && edge.Point == new ScreenPoint(.5, .1) && edge.Distance < 5, "Edge insertion did not use the nearest point on the segment.");
        var shifted = PointEditing.Translate(concave, .2, -.2);
        shifted.ValidatePolygon();
        Check(shifted.Vertices.Count == 5 && shifted.Vertices[3] == new ScreenPoint(.6, .4), "Mat drag omitted added points or failed bounds clamping.");
        var many = new PointOptions { Enabled = true, Vertices = Enumerable.Range(0, 40).Select(i => new ScreenPoint(.5 + .4 * Math.Cos(i * Math.PI / 20), .5 + .4 * Math.Sin(i * Math.PI / 20))).ToArray() };
        many.ValidatePolygon();
        Check(PolygonGeometry.Triangles(many).Length == 38 * 3 && PointEditing.Hits(many, many.Vertices[37].X, many.Vertices[37].Y, 1920, 1080, .1).SequenceEqual([37]), "Mat points retained a four/32-point limit.");
        Check(many == JsonSerializer.Deserialize<PointOptions>(JsonSerializer.Serialize(many)), "Point round-trip did not preserve geometry/equality.");
        try { (concave with { Vertices = [new(0, 0), new(1, 1), new(0, 1), new(1, 0)] }).ValidatePolygon(); throw new Exception("Crossing mat edges were accepted."); }
        catch (ArgumentException) { }
        var folder = Path.Combine(Path.GetTempPath(), "ExperienceX-mat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "appsettings.json");
            File.WriteAllText(path, """
                {"ConfigurationVersion":3,"configurationMode":true,"KeystoneHitDiameterPercent":5,
                 "DisplayScreens":{"TEST":[{"Id":"video","Name":"screen1","Type":"video","Keystone":{"Enabled":true,
                 "TopLeft":{"X":0.1,"Y":0.2},"TopRight":{"X":0.9,"Y":0.1},"BottomRight":{"X":0.8,"Y":0.9},"BottomLeft":{"X":0.2,"Y":0.8}}}]},"Custom":23}
                """);
            var store = new PointConfigurationStore(path);
            var options = ExperienceOptions.Parse(store.EnsureDisplays(["TEST"]));
            var root = JsonNode.Parse(File.ReadAllText(path))!;
            Check(options.PointsFor("TEST").TopLeft == new ScreenPoint(.1, .2) && root["KeystoneHitDiameterPercent"] is null &&
                root["DisplayScreens"]!["TEST"]![0]!["Keystone"] is null && root["DisplayScreens"]!["TEST"]![0]!["Points"]!["Vertices"]!.AsArray().Count == 4,
                "Existing video coordinates were lost or kept obsolete point names during migration.");
            var mat = await store.AddScreenAsync("TEST", type: "mat");
            Check(mat.IsMat && mat.Points.TopLeft == new ScreenPoint(.25, .25), "Mat creation did not share normal naming/initial geometry.");
            await store.SaveAsync("TEST", mat.Points, concave, mat.Id);
            options = ExperienceOptions.Parse(store.EnsureDisplays(["TEST"]));
            Check(options.PointsFor("TEST", mat.Id) == concave && JsonNode.Parse(File.ReadAllText(path))!["Custom"]!.GetValue<int>() == 23, "Mat persistence/startup lost added points or unrelated settings.");
            try { await store.SaveAsync("TEST", options.PointsFor("TEST", "video"), concave, "video"); throw new Exception("A video screen accepted polygon points."); }
            catch (ArgumentException) { }
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine("Passed point migration, mat creation/persistence, concave selection/triangulation, edge projection, whole-polygon bounds, crossing rejection and 40-point geometry.");
    }
}
