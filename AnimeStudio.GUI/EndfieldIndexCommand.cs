using System;
using System.IO;
using System.Threading;

namespace AnimeStudio.GUI
{
    internal static class EndfieldIndexCommand
    {
        private sealed class Progress : IProgress<EndfieldIndexProgress>
        {
            public EndfieldIndexProgress Last;
            public void Report(EndfieldIndexProgress value)
            {
                Last = value;
                if (value.ProcessedBundles % 1024 == 0 || value.ProcessedBundles == value.TotalBundles)
                    Console.WriteLine($"INDEX-PROGRESS {value.ProcessedBundles}/{value.TotalBundles} parsed={value.ParsedBundles} reused={value.ReusedBundles} removed={value.RemovedBundles}");
            }
        }
        public static int Run(string[] args)
        {
            if (args.Length != 3) { Console.Error.WriteLine("Usage: --update-eff-index <game-or-VFS> <workspace>");return 2; }
            try
            {
                var archive = EndfieldVfsArchive.Open(args[1]);var progress = new Progress();
                var index = EndfieldVfsAssetIndexBuilder.BuildAsync(archive, args[2], GameManager.GetGameByType(GameType.ArknightsEndfield), progress, CancellationToken.None).GetAwaiter().GetResult();
                EndfieldSourceRefresh.Refresh(archive, args[2], null, CancellationToken.None);
                Console.WriteLine($"INDEX-PREPARED path={index} parsed={progress.Last.ParsedBundles} reused={progress.Last.ReusedBundles} removed={progress.Last.RemovedBundles} ms={progress.Last.Elapsed.TotalMilliseconds:F2}");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine("INDEX-REJECT reason=" + error);return 1; }
        }
    }
}
