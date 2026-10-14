using System.Buffers.Binary;

namespace PhotoBooth.Networking;

public static class DdpFrames
{
    public const int Port = 4048;
    // Fits below the Ethernet MTU without IP fragmentation; three RGB bytes per pixel.
    public const int PayloadBytes = 1440;
    public static IEnumerable<byte[]> Packets(byte[] rgb, int sequence)
    {
        if (rgb.Length == 0 || rgb.Length % 3 != 0 || rgb.Length > 65535 * 3)
            throw new ArgumentException("DDP frames require 1–65535 RGB pixels.");
        for (int offset = 0; offset < rgb.Length; offset += PayloadBytes)
        {
            int length = Math.Min(PayloadBytes, rgb.Length - offset);
            var packet = new byte[10 + length];
            packet[0] = (byte)(0x40 | (offset + length == rgb.Length ? 1 : 0));
            packet[1] = (byte)(1 + ((sequence - 1) % 15 + 15) % 15);
            packet[2] = 0x0B; // 8-bit RGB24
            packet[3] = 1; // display data; not a WLED segment ID
            BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), (uint)offset);
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(8), (ushort)length);
            rgb.AsSpan(offset, length).CopyTo(packet.AsSpan(10));
            yield return packet;
        }
    }
}
