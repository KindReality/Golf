using System.ComponentModel;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;

namespace ExperienceX;

internal static class GForceControlEndpoint
{
    internal static int FindPort(int processId)
    {
        if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
        int length = 0;
        var result = GetExtendedTcpTable(0, ref length, false, 2, 5, 0); // IPv4, OWNER_PID_ALL.
        if (result != 122 || length < 4 || length > 16 * 1024 * 1024) throw new Win32Exception((int)result);
        var memory = Marshal.AllocHGlobal(length);
        try
        {
            result = GetExtendedTcpTable(memory, ref length, false, 2, 5, 0);
            if (result != 0) throw new Win32Exception((int)result);
            var count = Marshal.ReadInt32(memory);
            if (count < 0 || count > (length - 4) / 24) throw new InvalidDataException("Invalid Windows TCP inventory.");
            var ports = new HashSet<int>();
            for (int i = 0; i < count; i++)
            {
                var row = memory + 4 + i * 24;
                if (Marshal.ReadInt32(row) != 2 || Marshal.ReadInt32(row, 20) != processId) continue;
                var address = new IPAddress(unchecked((uint)Marshal.ReadInt32(row, 4)));
                if (!address.Equals(IPAddress.Any) && !IPAddress.IsLoopback(address)) continue;
                var port = (ushort)IPAddress.NetworkToHostOrder(unchecked((short)Marshal.ReadInt32(row, 8)));
                if (port > 0) ports.Add(port);
            }
            return ports.Count == 1 ? ports.Single() : throw new InvalidOperationException("Owned G-Force process must have exactly one local TCP listener.");
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(nint buffer, ref int length, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int table, uint reserved);
}
