using System.Numerics;

namespace ExperienceX;

internal sealed class KeystoneTransformCache
{
    private KeystoneOptions? _key;
    private (Vector4, Vector4, Vector4) _inverse;
    private long _computations;
    public long Computations => Interlocked.Read(ref _computations);
    public (Vector4, Vector4, Vector4) Get(KeystoneOptions geometry)
    {
        if (_key != geometry)
        {
            _inverse = VideoTransform.Inverse(geometry);
            _key = geometry;
            Interlocked.Increment(ref _computations);
        }
        return _inverse;
    }
}
