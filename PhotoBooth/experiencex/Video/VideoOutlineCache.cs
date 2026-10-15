namespace ExperienceX;

// Geometry and framing stay static between edits; playback must not rebuild them every frame.
internal sealed class VideoOutlineCache
{
    private PointOptions? _shape;
    private int _width, _height;
    private double _aspect;
    private VideoFramingOptions? _requested, _covered;
    internal (double Aspect, VideoFramingOptions Framing) Get(PointOptions shape, int width, int height, VideoFramingOptions requested)
    {
        if (_shape != shape || _width != width || _height != height)
        {
            _aspect=VideoFramingOptions.ScreenAspect(shape,width,height);
            _shape=shape; _width=width; _height=height; _requested=null;
        }
        if (_requested != requested)
        {
            _covered=VideoOutlineGeometry.Cover(shape,requested,_aspect);
            _requested=requested;
        }
        return (_aspect,_covered!);
    }
}
