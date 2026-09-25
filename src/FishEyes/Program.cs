namespace FishEyes;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
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
}
