using System.IO.Ports;
using System.Text;

namespace Controller.Hardware;

internal interface ISerialLink : IDisposable
{
    byte ReadByte();
    void Write(byte[] bytes);
}

internal sealed class SerialLink : ISerialLink
{
    private readonly SerialPort _port;
    internal SerialLink(string port, int baudRate, bool dtrEnable)
    {
        _port = new(port, baudRate, Parity.None, 8, StopBits.One)
        {
            DtrEnable = dtrEnable, ReadTimeout = 250, WriteTimeout = 250,
            Encoding = new UTF8Encoding(false, true), NewLine = "\n"
        };
        try { _port.Open(); }
        catch { _port.Dispose(); throw; }
    }
    public byte ReadByte() => (byte)_port.ReadByte();
    public void Write(byte[] bytes) { _port.Write(bytes, 0, bytes.Length); _port.Write("\n"); }
    public void Dispose() => _port.Dispose();
}
