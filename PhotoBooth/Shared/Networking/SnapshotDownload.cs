using System.IO;
using System.Net.Http;

namespace PhotoBooth.Networking;

internal static class SnapshotDownload
{
    public static async Task<byte[]> ReadAsync(HttpResponseMessage response, int maximumBytes, CancellationToken token)
    {
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maximumBytes) throw new IOException("Snapshot exceeds its size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var memory = new MemoryStream(); var buffer = new byte[16384]; int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (memory.Length + read > maximumBytes) throw new IOException("Snapshot exceeds its size limit.");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }
}
