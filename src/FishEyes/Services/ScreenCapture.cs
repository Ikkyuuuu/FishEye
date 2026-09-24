using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace FishEyes;

public static class Native
{
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("dwmapi.dll")] public static extern int DwmFlush();
}

public static class ScreenCapture
{
    public static Mat Capture(Rectangle bounds)
    {
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        return FromBitmap(bitmap);
    }
    public static Mat FromBitmap(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            using var wrapper = Mat.FromPixelData(bitmap.Height, bitmap.Width, MatType.CV_8UC3, data.Scan0, data.Stride);
            return wrapper.Clone();
        }
        finally { bitmap.UnlockBits(data); }
    }
}

public record BoardFrame(Recognition Recognition, Rectangle Bounds, double GridScore);

public sealed class ScreenScanner : IDisposable
{
    private readonly BoardDetector detector = new();
    private readonly Lazy<PieceRecognizer> recognizer = new(() => new PieceRecognizer());
    private BoardFrame? previous;
    public BoardFrame? Scan(Rectangle[] monitors)
    {
        BoardFrame? best = null;
        foreach (var monitor in monitors)
        {
            using var screen = ScreenCapture.Capture(monitor);
            OpenCvSharp.Rect? old = previous is not null && monitor.Contains(previous.Bounds)
                ? new(previous.Bounds.X - monitor.X, previous.Bounds.Y - monitor.Y, previous.Bounds.Width, previous.Bounds.Height) : null;
            var board = detector.Detect(screen, old);
            if (board is null) continue;
            var recognition = recognizer.Value.Recognize(screen, board.Bounds, previous?.Recognition);
            if (recognition.MinConfidence < .60 || recognition.MeanConfidence < .95) continue;
            if (recognition.Position.InvalidReason(true) is not null && recognition.Position.InvalidReason(false) is not null) continue;
            var bounds = new Rectangle(monitor.X + board.Bounds.X, monitor.Y + board.Bounds.Y, board.Bounds.Width, board.Bounds.Height);
            var frame = new BoardFrame(recognition, bounds, board.Score);
            if (best is null || frame.Bounds.Width * frame.GridScore > best.Bounds.Width * best.GridScore) best = frame;
        }
        if (best is not null) previous = best;
        return best;
    }
    public void Dispose() { if (recognizer.IsValueCreated) recognizer.Value.Dispose(); }
}
