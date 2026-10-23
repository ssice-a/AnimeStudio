using System;
using System.Threading;

namespace AnimeStudio.GUI;

internal static class EffIndexExchangeCommand
{
    internal static int Run(string[] args)
    {
        try
        {
            if (args[0] == "--pack-eff-index" && args.Length == 5)
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(EndfieldPortableIndex.Pack(EndfieldVfsArchive.Open(args[1]), args[2], args[3], args[4])));
            else if (args[0] == "--import-eff-index" && args.Length == 4)
            {
                var archive = EndfieldVfsArchive.Open(args[1]);
                Console.WriteLine(EndfieldPortableIndex.Import(archive, args[2], args[3], CancellationToken.None));
                EndfieldSharedIndexes.TryRemember(archive, args[2]);
            }
            else if (args[0] == "--refresh-eff-sources" && args.Length == 4)
                Console.WriteLine("UPDATED " + EndfieldSourceRefresh.Refresh(EndfieldVfsArchive.Open(args[1]), args[2], null, CancellationToken.None, args[3]));
            else throw new ArgumentException("Usage: --pack-eff-index GAME WORKSPACE OUTPUT VERSION | --import-eff-index GAME WORKSPACE PACKAGE | --refresh-eff-sources GAME WORKSPACE OUTPUT_ROOT");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error);return 1; }
    }
}
