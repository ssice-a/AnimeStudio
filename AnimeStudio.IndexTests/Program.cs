try
{
    if (args.Length == 3 && args[0] == "--real") return IncrementalIndexTests.Real(args[1], args[2]);
    if (args.Length != 0) throw new ArgumentException("Usage: [--real <game-path> <new-output-directory>]");
    VfsFormatTests.Run();IndexDistributionTests.Run().GetAwaiter().GetResult();return IncrementalIndexTests.Run();
}
catch (Exception error) { Console.Error.WriteLine("INCREMENTAL-INDEX failed: " + error);return 1; }
