namespace ExperienceX;

// Mask rendering for media cutouts and the visualization's opaque outside mask.
internal sealed class MaskLayer(GraphicsResources graphics) : IDisposable
{
    private readonly PolygonMesh _mesh = new(graphics);
    public void Draw(PointOptions shape, bool inverse = false, bool cutout = false)
    {
        _mesh.Configure(shape,inverse);
        if (_mesh.Count > 0) graphics.DrawMask(_mesh.Vertices!,_mesh.Count,cutout);
    }
    public void Dispose() => _mesh.Dispose();
}
