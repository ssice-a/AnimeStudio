using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AnimeStudio.GUI;

internal static class WorkspaceLockTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "eff-workspace-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var lockPath = Path.Combine(root, "endfield-index.lock");

        try
        {
            var heldLock = EndfieldVfsAssetIndexBuilder.AcquireWorkspaceLock(lockPath, CancellationToken.None);
            if (File.Exists(lockPath))
                throw new Exception("Workspace locking unexpectedly created a lock file");
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            {
                var wait = Task.Run(() =>
                {
                    try
                    {
                        using var ignored = EndfieldVfsAssetIndexBuilder.AcquireWorkspaceLock(lockPath, cancellation.Token);
                        return false;
                    }
                    catch (OperationCanceledException)
                    {
                        return true;
                    }
                });

                if (!wait.GetAwaiter().GetResult())
                    throw new Exception("Waiting for a held workspace lock did not honor cancellation");
            }

            heldLock.Dispose();
            var acquired = EndfieldVfsAssetIndexBuilder.AcquireWorkspaceLock(lockPath, CancellationToken.None);
            acquired.Dispose();

            using (var legacyWriter = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
            {
                var wait = Task.Run(() =>
                {
                    try
                    {
                        using var ignored = EndfieldVfsAssetIndexBuilder.AcquireWorkspaceLock(lockPath, cancellation.Token);
                        return false;
                    }
                    catch (OperationCanceledException)
                    {
                        return true;
                    }
                });

                if (!wait.GetAwaiter().GetResult())
                    throw new Exception("Workspace lock did not wait for a client using the legacy lock file");
            }

            using (var legacyWriter = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                using var firstStarted = new ManualResetEventSlim();
                using var secondStarted = new ManualResetEventSlim();
                var first = Task.Run(() =>
                {
                    firstStarted.Set();
                    using var acquired = EndfieldVfsAssetIndexBuilder.AcquireWorkspaceLock(lockPath, CancellationToken.None);
                    return true;
                });
                firstStarted.Wait();
                Thread.Sleep(150);

                var second = Task.Factory.StartNew(() =>
                {
                    secondStarted.Set();
                    using var acquired = EndfieldVfsAssetIndexBuilder.AcquireWorkspaceLock(lockPath, cancellation.Token);
                    return true;
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                secondStarted.Wait();
                // Keep the legacy marker locked across multiple retries with a
                // second client already waiting on the named mutex.
                Thread.Sleep(750);
                legacyWriter.Dispose();
                if (!first.Wait(TimeSpan.FromSeconds(3)) || !first.GetAwaiter().GetResult())
                    throw new Exception("Workspace lock did not recover after the legacy lock was released");
                if (!second.Wait(TimeSpan.FromSeconds(3)) || !second.GetAwaiter().GetResult())
                    throw new Exception("A queued workspace client was blocked after the legacy lock was released");
            }

            using var recovered = EndfieldVfsAssetIndexBuilder.AcquireWorkspaceLock(lockPath, CancellationToken.None);
            Console.WriteLine("PASS workspace lock: mutex, legacy lock compatibility, and cancellation");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
