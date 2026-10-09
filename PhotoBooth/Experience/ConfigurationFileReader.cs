using System.IO;

namespace Experience;

// Used by one background reload at a time. Periodic verification also catches edits
// that preserve both file length and timestamp.
internal sealed class ConfigurationFileReader
{
    private DateTime _writeTime;
    private long _length = -2;
    private DateTime _verified;
    private string _json = "{}";

    public string Read(string path)
    {
        var file = new FileInfo(path);
        var length = file.Exists ? file.Length : -1;
        var writeTime = file.Exists ? file.LastWriteTimeUtc : DateTime.MinValue;
        if (length == _length && writeTime == _writeTime &&
            DateTime.UtcNow - _verified < TimeSpan.FromSeconds(10)) return _json;
        var json = length < 0 ? "{}" : File.ReadAllText(path);
        _json = json;
        _length = length;
        _writeTime = writeTime;
        _verified = DateTime.UtcNow;
        return json;
    }
}
