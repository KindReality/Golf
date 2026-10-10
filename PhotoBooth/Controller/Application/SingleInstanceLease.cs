namespace Controller.Application;

internal sealed class SingleInstanceLease : IDisposable
{
    private readonly Mutex _mutex;
    private SingleInstanceLease(Mutex mutex) => _mutex = mutex;
    public static SingleInstanceLease? TryAcquire()
    {
        var mutex = new Mutex(false, "Local\\HomeTechnologies.Controller");
        try
        {
            bool acquired;
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (acquired) return new(mutex);
            mutex.Dispose();
            return null;
        }
        catch { mutex.Dispose(); throw; }
    }
    public void Dispose() { _mutex.ReleaseMutex(); _mutex.Dispose(); }
}
