using PhotoBooth.Media;
using Forms = System.Windows.Forms;

namespace ExperienceX;

internal sealed class ScreenMediaBrowser
{
    private string _directory = "";
    private MediaLibraryEntry? _selection;
    private string? _binding;
    private bool _initialized;
    internal bool Active { get; private set; }
    internal string Label => _selection is { IsFolder:true } folder ? "Folder: " + folder.RelativePath + "/"
        : _selection is { RelativePath:"" } ? "No media"
        : _selection?.RelativePath ?? (_directory.Length>0 ? "Folder: " + _directory + "/" : "Media");
    internal void Reconcile(ScreenMediaCatalog catalog, ScreenOptions screen)
    {
        var binding = screen.MediaFile ?? catalog.Match(screen)?.RelativePath;
        if (!_initialized || !Active || _binding != binding)
        {
            _directory = string.IsNullOrEmpty(binding) ? "" : MediaLibrary.Parent(binding);
            _selection = new(binding ?? "",false);
            _binding = binding; _initialized = true;
        }
        while (!catalog.HasFolder(_directory)) _directory = MediaLibrary.Parent(_directory);
        if (_selection is { RelativePath.Length:>0 } selected &&
            !catalog.Entries(_directory).Any(e=>e.RelativePath.Equals(selected.RelativePath,StringComparison.OrdinalIgnoreCase))) _selection=null;
    }
    // Null means navigation only; an empty path explicitly releases the media lock.
    internal string? Navigate(ScreenMediaCatalog catalog, ScreenOptions screen, Forms.Keys key)
    {
        Reconcile(catalog,screen);
        Active=true;
        if (key==Forms.Keys.Left)
        {
            if (_directory.Length>0) { _selection=new(_directory,true); _directory=MediaLibrary.Parent(_directory); }
            return null;
        }
        if (key==Forms.Keys.Right)
        {
            if (_selection?.IsFolder==true) { _directory=_selection.RelativePath; _selection=null; }
            return null;
        }
        var entries=catalog.Entries(_directory).ToList();
        if (_directory.Length==0) entries.Insert(0,new("",false));
        if (entries.Count==0) return null;
        var index=_selection is null ? -1 : entries.FindIndex(e=>e.RelativePath.Equals(_selection.RelativePath,StringComparison.OrdinalIgnoreCase));
        if (index<0) index=key==Forms.Keys.Up ? entries.Count : -1;
        _selection=entries[(index+(key==Forms.Keys.Up?-1:1)+entries.Count)%entries.Count];
        if (_selection.IsFolder) return null;
        _binding=_selection.RelativePath;
        return _selection.RelativePath;
    }
}
