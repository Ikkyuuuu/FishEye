using System.Text.Json;

namespace FishEyes;

internal static class TestProgram
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        string? Value(string name)
        {
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
        string output = Path.GetFullPath(Value("--output") ?? "artifacts/test-results/tests.json");
        string sample = Value("--sample") ?? Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample-board.png");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        try
        {
            if (args.Contains("--gui-test")) return SelfTests.RunGui(sample, output);
            if (args.Contains("--window-test")) return SelfTests.RunGui(sample, output, windowMode: true);
            var report = args.Contains("--readme-image")
                ? ImageExample.RenderReadme(sample, Path.GetDirectoryName(output)!)
                : args.Contains("--build-themes")
                ? ThemePack.Build(sample, Path.GetDirectoryName(output)!)
                : args.Contains("--inspect-image")
                ? ImageExample.Inspect(sample)
                : args.Contains("--theme-tests")
                ? ThemeTests.Run(sample, Path.GetDirectoryName(output)!)
                : args.Contains("--analyze-image")
                ? ImageExample.AnalyzeAsync(sample, Path.GetDirectoryName(output)!).GetAwaiter().GetResult()
                : SelfTests.RunAsync(sample, args.Contains("--live-api"), Path.GetDirectoryName(output)!).GetAwaiter().GetResult();
            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(output, json);
            Console.WriteLine(json);
            return 0;
        }
        catch (Exception e)
        {
            string json = JsonSerializer.Serialize(new { passed = false, error = e.ToString() }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(output, json);
            Console.Error.WriteLine(json);
            return 1;
        }
    }
}
