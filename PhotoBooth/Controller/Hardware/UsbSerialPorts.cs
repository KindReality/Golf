using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Controller.Hardware;

internal sealed record UsbSerialPort(string Port, string InstanceId);

// Enumerate present Ports-class devices, then follow their PnP parents. This
// includes USB bridges such as FTDI without mistaking Bluetooth/PCI ports for USB.
internal static class UsbSerialPorts
{
    internal static UsbSerialPort[] Enumerate()
    {
        var ports = SerialPort.GetPortNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<UsbSerialPort>();
        var portsClass = new Guid("4d36e978-e325-11ce-bfc1-08002be10318");
        var devices = SetupDiGetClassDevs(ref portsClass, null, 0, 2); // DIGCF_PRESENT
        if (devices == new nint(-1)) throw new System.ComponentModel.Win32Exception();
        try
        {
            for (uint index = 0; ; ++index)
            {
                var info = new DeviceInfo { Size = (uint)Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(devices, index, ref info))
                {
                    if (Marshal.GetLastWin32Error() == 259) break; // ERROR_NO_MORE_ITEMS
                    throw new System.ComponentModel.Win32Exception();
                }
                var id = DeviceId(info.DevInst);
                if (!HasUsbParent(info.DevInst)) continue;
                var handle = SetupDiOpenDevRegKey(devices, ref info, 1, 0, 1, 0x20019);
                if (handle == new nint(-1)) continue;
                using var safeHandle = new SafeRegistryHandle(handle, true);
                using var key = RegistryKey.FromHandle(safeHandle);
                if (key.GetValue("PortName") is string port && ports.Contains(port))
                    result.Add(new(port, id));
            }
        }
        finally { SetupDiDestroyDeviceInfoList(devices); }
        return result.DistinctBy(p => p.Port, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool HasUsbParent(uint device)
    {
        for (var depth = 0; depth < 32; ++depth)
        {
            if (DeviceId(device).StartsWith(@"USB\VID_", StringComparison.OrdinalIgnoreCase)) return true;
            if (CM_Get_Parent(out var parent, device, 0) != 0) return false;
            device = parent;
        }
        return false;
    }
    private static string DeviceId(uint device)
    {
        var buffer = new StringBuilder(512);
        return CM_Get_Device_ID(device, buffer, buffer.Capacity, 0) == 0 ? buffer.ToString() : "";
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { public uint Size; public Guid ClassGuid; public uint DevInst; public nuint Reserved; }
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(ref Guid guid, string? enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiEnumDeviceInfo(nint devices, uint index, ref DeviceInfo info);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern nint SetupDiOpenDevRegKey(nint devices, ref DeviceInfo info, uint scope, uint profile, uint type, int access);
    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetupDiDestroyDeviceInfoList(nint devices);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_IDW")]
    private static extern uint CM_Get_Device_ID(uint device, StringBuilder id, int length, uint flags);
    [DllImport("cfgmgr32.dll")]
    private static extern uint CM_Get_Parent(out uint parent, uint device, uint flags);
}
