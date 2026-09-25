namespace FishEyes;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--engine-check")
            return CheckEngineAsync(Path.GetFullPath(args[1])).GetAwaiter().GetResult();
        ApplicationConfiguration.Initialize();
        try
        {
            using var mutex = new Mutex(true, @"Local\FishEyesCSharpOverlayV2", out bool first);
            if (!first) { MessageBox.Show("FishEyes is already running. Close its existing window first.", "FishEyes"); return 0; }
            bool windowMode = args.Contains("--window", StringComparer.OrdinalIgnoreCase);
            Application.Run(new MainForm(windowMode: windowMode));
            return 0;
        }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, "FishEyes could not start", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
    // Headless verification also works from the published single-file executable.
    private static async Task<int> CheckEngineAsync(string output)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        object report;
        int exitCode;
        try
        {
            using var archive = typeof(Program).Assembly.GetManifestResourceStream("FishEyes.Stockfish.zip")!;
            string archiveSha256 = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(archive));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            using var engine = new EngineService(null);
            var position = ChessPosition.Parse("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR");
            var white = await engine.AnalyzeAsync(position, true, 40);
            var black = await engine.AnalyzeAsync(position, false, 40);
            bool cached = (await engine.AnalyzeAsync(position, true, 40)).FromCache;
            report = new { passed = cached && engine.RequestCount == 2, version = StockfishBundle.Version,
                archiveSha256, white = white.Value, black = black.Value, elapsedMs = watch.Elapsed.TotalMilliseconds };
            exitCode = cached && engine.RequestCount == 2 ? 0 : 1;
        }
        catch (Exception e) { report = new { passed = false, error = e.ToString() }; exitCode = 1; }
        File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return exitCode;
    }
}
