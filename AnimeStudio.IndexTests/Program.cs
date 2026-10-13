try { return IncrementalIndexTests.Run(); }
catch (Exception error) { Console.Error.WriteLine("INCREMENTAL-INDEX failed: " + error);return 1; }
