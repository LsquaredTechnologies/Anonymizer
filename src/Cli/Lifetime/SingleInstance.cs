using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace Anonymizer.Cli.Lifetime;

internal static class SingleInstance
{
    /// <summary>
    /// Returns true if this process is the only running instance.
    /// </summary>
    public static IDisposable TryAcquire(string name)
    {
        if (OperatingSystem.IsWindows()) return AcquireWindowsMutex(name);
        if (OperatingSystem.IsLinux()) return AcquireLinuxLockFile(name);
        throw new PlatformNotSupportedException();
    }

    [SupportedOSPlatform("Windows")]
    private static IDisposable AcquireWindowsMutex(string name)
    {
        try
        {
            Mutex mutex = new(
                false,
                $"Global\\Anonymizer.{name}.Instance",
                new() { CurrentUserOnly = true },
                out var createdNew);

            return new Disposable(() =>
            {
                mutex?.ReleaseMutex();
                mutex?.Dispose();
            });
        }
        catch
        {
            return new NoopDisposable();
        }
    }

    [SupportedOSPlatform("Linux")]
    private static IDisposable AcquireLinuxLockFile(string name)
    {
        try
        {
            FileInfo lockFile = new(Path.Join(Application.Install.Path, Path.ChangeExtension(name, ".lock")));
            lockFile.Directory!.Create();
            var stream = new FileStream(
                lockFile.FullName,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);

            return new Disposable(() =>
            {
                stream.Dispose();
                if (lockFile.Exists)
                    lockFile.Delete();
            });
        }
        catch (IOException)
        {
            // File is locked by another instance
            return new NoopDisposable();
        }
        catch
        {
            return new NoopDisposable();
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class Disposable(Action action) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                action();
            }
            catch
            {
                // noop
            }
        }
    }
}
