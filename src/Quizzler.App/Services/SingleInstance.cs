using System.Security.Cryptography;
using System.Text;

namespace Quizzler.App.Services;

/// <summary>
/// One running app per database. Launching again signals the running copy to open its quiz window.
/// Keyed on the database path so a test instance (--db scratch.db) doesn't collide with the real one.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private readonly RegisteredWaitHandle _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle showEvent, Action onShowRequested)
    {
        _mutex = mutex;
        _showEvent = showEvent;
        _registration = ThreadPool.RegisterWaitForSingleObject(
            showEvent, (_, _) => onShowRequested(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Returns null if another instance already owns this database.</summary>
    public static SingleInstance? TryStart(string dbPath, Action onShowRequested)
    {
        var key = Key(dbPath);
        var mutex = new Mutex(initiallyOwned: true, $@"Local\Quizzler-{key}", out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }
        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\Quizzler-{key}-show");
        return new SingleInstance(mutex, showEvent, onShowRequested);
    }

    /// <summary>Asks the already-running instance to show its window.</summary>
    public static void SignalExisting(string dbPath)
    {
        if (EventWaitHandle.TryOpenExisting($@"Local\Quizzler-{Key(dbPath)}-show", out var handle))
        {
            using (handle) handle.Set();
        }
    }

    private static string Key(string dbPath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(dbPath).ToLowerInvariant())))[..16];

    public void Dispose()
    {
        _registration.Unregister(null);
        _showEvent.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
