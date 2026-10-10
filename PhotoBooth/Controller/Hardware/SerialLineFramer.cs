namespace Controller.Hardware;

// Discard an oversized line through its newline rather than allowing unbounded serial input.
internal sealed class SerialLineFramer(int maximumBytes)
{
    private readonly byte[] _buffer = new byte[maximumBytes + 1];
    private int _length;
    private bool _discarding;
    public byte[]? Push(byte value, out bool oversized)
    {
        oversized = false;
        if (value == (byte)'\n')
        {
            var length = _length > 0 && _buffer[_length - 1] == '\r' ? _length - 1 : _length;
            var line = !_discarding && length > 0 ? _buffer.AsSpan(0, length).ToArray() : null;
            _length = 0;
            _discarding = false;
            return line;
        }
        if (_discarding) return null;
        if (_length > maximumBytes || _length == maximumBytes && value != (byte)'\r')
        {
            _discarding = true;
            oversized = true;
            return null;
        }
        _buffer[_length++] = value;
        return null;
    }
}
