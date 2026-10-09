using PhotoBooth.Diagnostics;
using Forms = System.Windows.Forms;

namespace ExperienceX;

internal sealed class KeystoneCalibration(Func<ExperienceOptions> options,
    Func<IEnumerable<IKeystoneSurface>> windows, KeystoneConfigurationStore store, Func<Task> reload) : IAsyncDisposable
{
    private readonly AsyncTaskScope _tasks = new("Calibration");
    private bool _stopping;
    public void RequestFinish(IKeystoneSurface window) => _tasks.Run(_ => FinishAsync(window), "Save calibration");
    private readonly Dictionary<string, KeystoneHitCycle> _cycles = new(StringComparer.OrdinalIgnoreCase);
    private IKeystoneSurface? _active;
    private KeystoneOptions? _original, _draft;
    private int _selected;
    private bool _saving;
    private string? _localSaveMonitor;
    private KeystoneOptions? _localSaveCoordinates;
    private double _dragOffsetX, _dragOffsetY;
    private readonly Dictionary<string, List<KeystoneOptions>> _undo = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _revisions = new(StringComparer.OrdinalIgnoreCase);
    private void Show(IKeystoneSurface owner, int selected)
    {
        foreach (var window in windows())
            window.ShowCalibration(window == owner ? selected : -1);
    }
    public bool Begin(IKeystoneSurface window, double x, double y)
    {
        if (_stopping || !options().ConfigurationMode || _saving)
            return false;
        Cancel("NewSelection");
        var original = options().KeystoneFor(window.MonitorName);
        if (!_cycles.TryGetValue(window.MonitorName, out var cycle))
            _cycles[window.MonitorName] = cycle = new();
        var selected = cycle.Pick(KeystoneEditing.Hits(original, x, y, window.PixelWidth, window.PixelHeight,
            options().KeystoneHitDiameterPercent));
        Show(window, selected);
        if (selected < 0)
            return false;
        _active = window;
        _original = original;
        _draft = original;
        _selected = selected;
        var point = KeystoneEditing.Points(original)[selected];
        _dragOffsetX = point.X - x;
        _dragOffsetY = point.Y - y;
        Telemetry.Info("KeystonePointSelected", new
        {
            window.MonitorName,
            Point = selected + 1
        });
        return true;
    }
    public void Move(IKeystoneSurface window, double x, double y)
        => MovePoint(window, x + _dragOffsetX, y + _dragOffsetY);
    private void MovePoint(IKeystoneSurface window, double x, double y)
    {
        if (_active != window || _draft is null || _saving)
            return;
        _draft = KeystoneEditing.Move(_draft, _selected, x, y);
        window.PreviewKeystone(_draft);
    }
    public async Task<bool> FinishAsync(IKeystoneSurface window, bool remember = true)
    {
        if (_stopping)
            return false;
        foreach (var item in windows())
            item.FadeCalibration();
        if (_active != window || _original is null || _draft is null || _saving)
            return false;
        var original = _original;
        var edited = _draft;
        var selected = _selected;
        _active = null;
        _original = null;
        _draft = null;
        window.ReleaseCalibrationCapture();
        if (edited == original)
        {
            window.PreviewKeystone(null);
            return false;
        }
        _saving = true;
        _localSaveMonitor = window.MonitorName;
        _localSaveCoordinates = edited;
        try
        {
            await store.SaveAsync(window.MonitorName, original, edited);
            if (_stopping)
                return false;
            await reload();
            if (_stopping)
                return false;
            window.PreviewKeystone(null);
            if (!await window.ConfirmCalibrationAsync(edited) || _stopping || options().KeystoneFor(window.MonitorName) != edited)
            {
                Telemetry.Warning("KeystoneConfirmationSuperseded", new
                {
                    window.MonitorName
                });
                return false;
            }
            if (remember)
            {
                if (!_undo.TryGetValue(window.MonitorName, out var history))
                    _undo[window.MonitorName] = history = [];
                history.Add(original);
                if (history.Count > 20)
                    history.RemoveAt(0);
            }
            if (selected < 0)
                window.FlashCalibrationPoints(15);
            else
                window.FlashCalibration(selected, true);
            Telemetry.Info("KeystoneSaved", new
            {
                window.MonitorName,
                Point = selected < 0 ? (int?)null : selected + 1,
                Coordinates = edited
            });
            return true;
        }
        catch (KeystoneEditConflictException ex)
        {
            window.PreviewKeystone(null);
            await reload();
            Telemetry.Warning("KeystoneEditCancelled", new
            {
                window.MonitorName,
                Reason = ex.Message
            });
        }
        catch (Exception ex)
        {
            if (_stopping)
                return false;
            window.PreviewKeystone(null);
            window.FlashCalibration(Math.Max(0, selected), false);
            Telemetry.Error("KeystoneSaveFailed", ex, new
            {
                window.MonitorName
            });
        }
        finally { _saving = false; _localSaveMonitor = null; _localSaveCoordinates = null; }
        return false;
    }
    public void Cancel(string reason)
    {
        var active = _active;
        _active = null;
        _original = null;
        _draft = null;
        if (active is null)
            return;
        active.ReleaseCalibrationCapture();
        active.PreviewKeystone(null);
        foreach (var window in windows())
            window.FadeCalibration();
        Telemetry.Info("KeystoneEditCancelled", new
        {
            active.MonitorName,
            Reason = reason
        });
    }
    public void ConfigurationChanged(ExperienceOptions next)
    {
        if (!next.ConfigurationMode || (_active is not null && next.KeystoneFor(_active.MonitorName) != _original))
            Cancel(next.ConfigurationMode ? "RemoteCoordinatesChanged" : "ConfigurationModeDisabled");
    }
    public Task ConfigurationAppliedAsync(ExperienceOptions previous, ExperienceOptions next)
    {
        if (_stopping)
            return Task.CompletedTask;
        var confirmations = new List<Task>();
        foreach (var window in windows())
        {
            // Creating initial monitor defaults is not a remote calibration save.
            if (!previous.DisplayKeystones.Keys.Any(name => string.Equals(name, window.MonitorName, StringComparison.OrdinalIgnoreCase)))
                continue;
            var before = previous.KeystoneFor(window.MonitorName);
            var after = next.KeystoneFor(window.MonitorName);
            if (before == after || (string.Equals(_localSaveMonitor, window.MonitorName, StringComparison.OrdinalIgnoreCase) && _localSaveCoordinates == after))
                continue;
            _undo.Remove(window.MonitorName);
            var revision = _revisions.GetValueOrDefault(window.MonitorName) + 1;
            _revisions[window.MonitorName] = revision;
            var oldPoints = KeystoneEditing.Points(before);
            var newPoints = KeystoneEditing.Points(after);
            var mask = 0;
            for (int i = 0; i < 4; i++)
                if (oldPoints[i] != newPoints[i])
                    mask |= 1 << i;
            if (mask == 0)
                mask = 15; // Enabled-only changes confirm at all four corners.
            confirmations.Add(ConfirmRemoteAsync(window, after, mask, revision));
        }
        return Task.WhenAll(confirmations);
    }
    private async Task ConfirmRemoteAsync(IKeystoneSurface window, KeystoneOptions coordinates, int mask, long revision)
    {
        try
        {
            if (!await window.ConfirmCalibrationAsync(coordinates) || _stopping || _revisions.GetValueOrDefault(window.MonitorName) != revision || options().KeystoneFor(window.MonitorName) != coordinates)
                return;
            window.FlashCalibrationPoints(mask);
            Telemetry.Info("KeystoneRemoteChangeApplied", new
            {
                window.MonitorName,
                Points = Enumerable.Range(0, 4).Where(i => (mask & (1 << i)) != 0).Select(i => i + 1).ToArray(),
                Coordinates = coordinates
            });
        }
        catch (Exception ex) { Telemetry.Error("KeystoneRenderConfirmationFailed", ex, new { window.MonitorName }); }
    }
    public async Task ResetAsync(IKeystoneSurface window)
    {
        if (!options().ConfigurationMode || _saving)
            return;
        Cancel("Reset");
        _active = window;
        _original = options().KeystoneFor(window.MonitorName);
        _draft = new();
        _selected = -1;
        window.PreviewKeystone(_draft);
        await FinishAsync(window);
    }
    public async Task UndoAsync(IKeystoneSurface window)
    {
        if (!options().ConfigurationMode || _saving)
            return;
        if (_active is not null)
        {
            Cancel("UndoDraft");
            return;
        }
        if (!_undo.TryGetValue(window.MonitorName, out var history) || history.Count == 0)
            return;
        var restore = history[^1];
        _active = window;
        _original = options().KeystoneFor(window.MonitorName);
        _draft = restore;
        _selected = -1;
        window.PreviewKeystone(restore);
        if (await FinishAsync(window, false))
            history.RemoveAt(history.Count - 1);
    }
    public bool Key(IKeystoneSurface window, Forms.Keys key)
    {
        if (_stopping || !options().ConfigurationMode || _saving)
            return false;
        var code = key & Forms.Keys.KeyCode;
        if ((key & (Forms.Keys.Control | Forms.Keys.Alt)) == 0 && code == Forms.Keys.Z)
        {
            _tasks.Run(_ => UndoAsync(window), "Undo calibration");
            return true;
        }
        if ((key & (Forms.Keys.Control | Forms.Keys.Alt)) == 0 && code == Forms.Keys.R)
        {
            _tasks.Run(_ => ResetAsync(window), "Reset calibration");
            return true;
        }
        if (code == Forms.Keys.Escape)
        {
            Cancel("Escape");
            foreach (var w in windows())
                w.FadeCalibration();
            return true;
        }
        if (code == Forms.Keys.Enter)
        {
            RequestFinish(window);
            return true;
        }
        if (code is not (Forms.Keys.Tab or Forms.Keys.Left or Forms.Keys.Right or Forms.Keys.Up or Forms.Keys.Down))
            return false;
        var existed = _active == window;
        if (!existed)
        {
            Cancel("KeyboardMonitorChanged");
            _active = window;
            _original = options().KeystoneFor(window.MonitorName);
            _draft = _original;
            _selected = 0;
        }
        if (code == Forms.Keys.Tab)
        {
            var backwards = (key & Forms.Keys.Shift) != 0;
            _selected = existed ? (_selected + (backwards ? 3 : 1)) % 4 : (backwards ? 3 : 0);
            Show(window, _selected);
            return true;
        }
        var p = KeystoneEditing.Points(_draft!)[_selected];
        var step = (key & Forms.Keys.Shift) != 0 ? 10 : 1;
        var dx = code == Forms.Keys.Left ? -step : code == Forms.Keys.Right ? step : 0;
        var dy = code == Forms.Keys.Up ? -step : code == Forms.Keys.Down ? step : 0;
        MovePoint(window, p.X + (double)dx / Math.Max(1, window.PixelWidth - 1), p.Y + (double)dy / Math.Max(1, window.PixelHeight - 1));
        Show(window, _selected);
        return true;
    }
    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        Cancel("Shutdown");
        await _tasks.DisposeAsync();
    }
}
