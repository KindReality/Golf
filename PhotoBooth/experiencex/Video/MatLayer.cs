using System.Numerics;
using Vortice.Direct3D11;

namespace ExperienceX;

// A persistent opaque mask. Only point changes rebuild geometry; idle masks need no render loop.
internal sealed class MatLayer(GraphicsResources graphics) : IDisposable
{
    private ID3D11Buffer? _vertices;
    private PointOptions? _shape;
    private int _count;
    private bool _inverse;
    public void Draw(PointOptions shape, bool inverse = false)
    {
        if (_shape != shape || _inverse != inverse)
        {
            var vertices = (inverse ? PolygonGeometry.OutsideTriangles(shape) : PolygonGeometry.Triangles(shape)).Select(p => new Vector2((float)(p.X * 2 - 1), (float)(1 - p.Y * 2))).ToArray();
            var replacement = vertices.Length > 0 ? graphics.Device.CreateBuffer<Vector2>(vertices, BindFlags.VertexBuffer) : null;
            graphics.Context.IASetVertexBuffer(0, null!, 0);
            _vertices?.Dispose(); _vertices = replacement; _count = vertices.Length; _shape = shape; _inverse = inverse;
        }
        if (_count > 0) graphics.DrawMat(_vertices!, _count);
    }
    public void Dispose() => _vertices?.Dispose();
}
