using ExperienceX;

internal static class SingleInstanceChecks
{
    public static void Run()
    {
        var name = "PhotoBooth.RestartTest." + Guid.NewGuid().ToString("N");
        using var lingeringHandle = new Mutex(false, name);
        var first = SingleInstanceLease.TryAcquire(name) ?? throw new Exception("An existing unowned mutex blocked startup.");
        try
        {
            // Task.Run(...).GetResult() can inline on the mutex owner's thread;
            // Windows mutexes are recursive, so use a genuinely different thread.
            bool duplicateAdmitted = false;
            Exception? duplicateFailure = null;
            var duplicateThread = new Thread(() =>
            {
                try
                {
                    using var duplicate = SingleInstanceLease.TryAcquire(name);
                    duplicateAdmitted = duplicate is not null;
                }
                catch (Exception ex) { duplicateFailure = ex; }
            });
            duplicateThread.Start();
            if (!duplicateThread.Join(TimeSpan.FromSeconds(5)))
                throw new Exception("Duplicate fixture did not exit.");
            if (duplicateFailure is not null)
                throw new Exception("Duplicate fixture failed.", duplicateFailure);
            if (duplicateAdmitted)
                throw new Exception("A running owner did not block a duplicate launch.");
        }
        finally { first.Dispose(); first.Dispose(); }
        using (var restarted = SingleInstanceLease.TryAcquire(name))
            if (restarted is null || restarted.RecoveredAbandonedOwner)
                throw new Exception("Normal restart failed with a lingering mutex handle.");

        var crashedOwner = new Thread(() =>
        {
            // Simulate Task Manager / process termination: owner exits without ReleaseMutex.
            _ = SingleInstanceLease.TryAcquire(name) ?? throw new Exception("Crash fixture could not acquire ownership.");
        });
        crashedOwner.Start();
        if (!crashedOwner.Join(TimeSpan.FromSeconds(5)))
            throw new Exception("Crash fixture did not exit.");
        using (var recovered = SingleInstanceLease.TryAcquire(name))
            if (recovered is null || !recovered.RecoveredAbandonedOwner)
                throw new Exception("An abandoned mutex blocked a new application instance.");
        using (var finalRestart = SingleInstanceLease.TryAcquire(name))
            if (finalRestart is null || finalRestart.RecoveredAbandonedOwner)
                throw new Exception("Recovery did not release ownership for the following restart.");
        Console.WriteLine("Passed duplicate exclusion, normal restart with retained handles, abandoned owner recovery, and restart after recovery against the application's instance guard.");
    }
}
