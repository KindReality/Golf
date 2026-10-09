namespace ExperienceX;

// Acquire and release on the application dispatcher: mutex ownership belongs to a thread.
internal sealed class SingleInstanceLease : IDisposable
{
    private Mutex? _mutex;
    public bool RecoveredAbandonedOwner
    {
        get;
    }
    private SingleInstanceLease(Mutex mutex, bool recovered)
    {
        _mutex = mutex;
        RecoveredAbandonedOwner = recovered;
    }

    public static SingleInstanceLease? TryAcquire(string name)
    {
        var mutex = new Mutex(false, name);
        try
        {
            bool acquired, recovered = false;
            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException) { acquired = true; recovered = true; }
            if (acquired)
                return new(mutex, recovered);
            // A rejected launch must not retain a handle while its message box is open.
            mutex.Dispose();
            return null;
        }
        catch { mutex.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_mutex is null)
            return;
        _mutex.ReleaseMutex();
        _mutex.Dispose();
        _mutex = null;
    }
}
