namespace FishEyes;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            using var mutex = new Mutex(true, @"Local\FishEyesCSharpOverlayV2", out bool first);
            if (!first) { MessageBox.Show("FishEyes is already running. Look for its floating panel.", "FishEyes"); return 0; }
            Application.Run(new MainForm());
            return 0;
        }
        catch (Exception e)
        {
            MessageBox.Show(e.Message, "FishEyes could not start", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
