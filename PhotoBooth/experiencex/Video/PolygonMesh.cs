using System.Numerics;
using Vortice.Direct3D11;

namespace ExperienceX;

// Shared cached triangles for mask rendering and clipped media; rebuilt only when the outline changes.
internal sealed class PolygonMesh(GraphicsResources graphics) : IDisposable
{
    private PointOptions? _shape;
    private bool _inverse;
    internal ID3D11Buffer? Vertices { get; private set; }
    internal int Count { get; private set; }
    internal void Configure(PointOptions shape, bool inverse = false)
    {
        if (_shape == shape && _inverse == inverse) return;
        var vertices = (inverse ? PolygonGeometry.OutsideTriangles(shape) : PolygonGeometry.Triangles(shape))
            .Select(p => new Vector2((float)(p.X*2-1),(float)(1-p.Y*2))).ToArray();
        var replacement = vertices.Length>0 ? graphics.Device.CreateBuffer<Vector2>(vertices,BindFlags.VertexBuffer) : null;
        graphics.Context.IASetVertexBuffer(0,null!,0);
        Vertices?.Dispose(); Vertices=replacement; Count=vertices.Length; _shape=shape; _inverse=inverse;
    }
    public void Dispose() { Vertices?.Dispose(); Vertices=null; Count=0; }
}
