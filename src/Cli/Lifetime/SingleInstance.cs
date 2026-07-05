using System.Runtime.Versioning;

namespace Anonymizer.Cli.Lifetime;

internal static class SingleInstance
{
    public static InstanceHandle TryAcquire(string name)
    {
        if (OperatingSystem.IsWindows()) return AcquireWindowsMutex(name);
        if (OperatingSystem.IsLinux()) return AcquireLinuxLockFile(name);
        throw new PlatformNotSupportedException();
    }

    [SupportedOSPlatform("Windows")]
    private static InstanceHandle AcquireWindowsMutex(string name)
    {
        try
        {
            Mutex mutex = new(true, $"Anonymizer.{name}.Instance", out bool createdNew);
            if (!createdNew)
            {
                mutex.Dispose();
                return InstanceHandle.NotAcquired();
            }

            return InstanceHandle.Acquired(new Disposable(() =>
            {
                mutex.ReleaseMutex();
                mutex.Dispose();
            }));
        }
        catch (Exception)
        {
            return InstanceHandle.NotAcquired();
        }
    }

    [SupportedOSPlatform("Linux")]
    private static InstanceHandle AcquireLinuxLockFile(string name)
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

            return InstanceHandle.Acquired(new Disposable(() =>
            {
                stream.Dispose();
                if (lockFile.Exists)
                    lockFile.Delete();
            }));
        }
        catch (IOException)
        {
            // File is locked by another instance
            return InstanceHandle.NotAcquired();
        }
        catch
        {
            return InstanceHandle.NotAcquired();
        }
    }

    internal sealed class InstanceHandle(bool isAcquired, IDisposable releaser) : IDisposable
    {
        public bool IsAcquired { get; } = isAcquired;

        public static InstanceHandle Acquired(IDisposable releaser) => new(true, releaser);

        public static InstanceHandle NotAcquired() => new(false, new NoopDisposable());

        public void Dispose() => releaser.Dispose();
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
