using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using PhotoBooth.Media;

namespace Controller.Scheduling;

// Shell metadata reads run on preparation workers, never on the deadline scheduler or UI.
internal static class MediaDurations
{
    private static readonly ConcurrentDictionary<string, (long Size, long Written, double? Duration)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Readers = new(2);
    internal static async Task<double?> ReadAsync(string relative, Func<string, double?> read, CancellationToken token)
    {
        if (!await Readers.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false)) return null;
        // A faulty shell extension cannot hold a playlist open forever or consume unlimited workers.
        var worker = Task.Run(() => { try { return read(relative); } finally { Readers.Release(); } });
        return await worker.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
    }
    internal static double? ReadMilliseconds(string relative)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Home Technologies", "Controller", "Media");
        return ReadPathMilliseconds(MediaLibrary.Resolve(root, relative));
    }
    internal static double? ReadPathMilliseconds(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) return null;
        if (Cache.TryGetValue(path, out var cached) && cached.Size == file.Length && cached.Written == file.LastWriteTimeUtc.Ticks) return cached.Duration;
        var initialized = CoInitializeEx(0, 0);
        IPropertyStore? store = null;
        var value = new PropertyValue();
        double? duration = null;
        try
        {
            var id = typeof(IPropertyStore).GUID;
            Marshal.ThrowExceptionForHR(PSGetPropertyKeyFromName("System.Media.Duration", out var key));
            Marshal.ThrowExceptionForHR(SHGetPropertyStoreFromParsingName(path, 0, 0, ref id, out store));
            Marshal.ThrowExceptionForHR(store.GetValue(ref key, out value));
            if (value.Type == 21 && value.Unsigned > 0) duration = value.Unsigned / 10000.0;
        }
        catch (COMException) { }
        finally
        {
            PropVariantClear(ref value);
            if (store is not null) Marshal.ReleaseComObject(store);
            if (initialized >= 0) CoUninitialize();
        }
        if (Cache.Count > 4096) Cache.Clear();
        Cache[path] = (file.Length, file.LastWriteTimeUtc.Ticks, duration);
        return duration;
    }
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropertyValue
    { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public ulong Unsigned; }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropertyValue value);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)] private static extern int SHGetPropertyStoreFromParsingName(string path, nint context, uint flags, ref Guid id, out IPropertyStore store);
    [DllImport("propsys.dll", CharSet = CharSet.Unicode)] private static extern int PSGetPropertyKeyFromName(string name, out PropertyKey key);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropertyValue value);
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
}
