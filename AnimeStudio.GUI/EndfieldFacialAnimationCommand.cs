using AnimeStudio;
using System;
using System.IO;
using Newtonsoft.Json;

namespace AnimeStudio.GUI;

internal static class EndfieldFacialAnimationCommand
{
    internal static int Run(string[] args)
    {
        if (args.Length != 6 && (args.Length != 8 || args[6] != "--unity-version" || string.IsNullOrWhiteSpace(args[7])))
        {
            Console.Error.WriteLine("Usage: --export-eff-animations <vfs-root> <workspace> <logical-facial-avatar> <selection.json> <output.eanim> [--unity-version <version>]");
            return 2;
        }
        try
        {
            Logger.Default = new ConsoleLogger(); Logger.Flags = LoggerEvent.Error;
            var count = EiemFacialAnimationExporter.Export(EndfieldVfsArchive.Open(Path.GetFullPath(args[1])),
                Path.GetFullPath(args[2]), args[3], Path.GetFullPath(args[5]),
                JsonConvert.DeserializeObject<string[]>(File.ReadAllText(args[4])), unityVersion: args.Length == 8 ? args[7] : null,
                progress: Console.WriteLine);
            Console.WriteLine($"Exported {count} facial animations to {Path.GetFullPath(args[5])}");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
