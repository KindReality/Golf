using System.Drawing;
using System.Windows.Threading;
using ExperienceX;

internal static class IdleMouseCursorChecks
{
    public static void Run()
    {
        bool focused = false;
        var position = new Point(10, 20);
        var elapsed = TimeSpan.Zero;
        List<bool> transitions = [];
        using var cursor = new IdleMouseCursor(Dispatcher.CurrentDispatcher, () => focused, () => position,
            () => elapsed, hidden => transitions.Add(hidden));
        void Poll(double seconds) { elapsed = TimeSpan.FromSeconds(seconds); cursor.Poll(); }
        Poll(0); Poll(10);
        if (transitions.Count != 0) throw new Exception("Unfocused app hid the cursor.");
        focused = true;
        Poll(10); Poll(14.999);
        if (transitions.Count != 0) throw new Exception("Cursor hid before five seconds of focused inactivity.");
        Poll(15); Poll(16);
        if (!transitions.SequenceEqual([true])) throw new Exception("Idle cursor was not hidden once, or Hide calls were unbalanced.");
        position = new Point(11, 20);
        Poll(16.1);
        if (!transitions.SequenceEqual([true, false])) throw new Exception("Mouse movement did not restore the pointer.");
        Poll(21.1);
        focused = false;
        Poll(21.2);
        if (!transitions.SequenceEqual([true, false, true, false])) throw new Exception("Leaving Experience did not restore the pointer.");
        focused = true;
        Poll(30); Poll(34.9);
        if (transitions.Count != 4) throw new Exception("Focus return failed to restart the idle countdown.");
        Poll(35);
        cursor.Dispose(); cursor.Dispose(); Poll(50);
        if (!transitions.SequenceEqual([true, false, true, false, true, false])) throw new Exception("Shutdown did not balance cursor visibility or stop updates.");
        Console.WriteLine("Passed five-second focused cursor idle, movement/focus restoration, focus return and balanced shutdown checks.");
    }
}
