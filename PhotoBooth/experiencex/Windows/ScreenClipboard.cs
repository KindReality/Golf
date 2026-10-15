using Forms = System.Windows.Forms;

namespace ExperienceX;

// Custom JSON string format avoids interpreting arbitrary clipboard text or serialized application objects.
internal static class ScreenClipboard
{
    internal static void Write(ScreenCopy screen)
    {
        var data = new Forms.DataObject();
        data.SetData(ScreenCopy.ClipboardFormat, false, screen.Encode());
        Forms.Clipboard.SetDataObject(data, true);
    }
    internal static ScreenCopy Read()
    {
        var data = Forms.Clipboard.GetDataObject();
        if (data?.GetDataPresent(ScreenCopy.ClipboardFormat, false) != true ||
            data.GetData(ScreenCopy.ClipboardFormat, false) is not string json)
            throw new ArgumentException("Copy a video, image or mat screen in Experience first.");
        return ScreenCopy.Decode(json);
    }
}
