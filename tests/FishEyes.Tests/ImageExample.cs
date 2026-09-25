using System.Drawing.Imaging;
using OpenCvSharp;

namespace FishEyes;

internal static class ImageExample
{
    // Render the actual application controls off-screen. Windows deliberately
    // omits live overlay windows from screenshots, including README captures.
    public static object RenderReadme(string input, string outputDirectory)
    {
        using var source = Cv2.ImDecode(File.ReadAllBytes(input), ImreadModes.Color);
        var board = new BoardDetector().Detect(source) ?? throw new InvalidOperationException("No board detected.");
        using var recognizer = new PieceRecognizer();
        var recognition = recognizer.Recognize(source, board.Bounds);
        var bounds = new Rectangle(board.Bounds.X, board.Bounds.Y, board.Bounds.Width, board.Bounds.Height);
        var frame = new BoardFrame(recognition, bounds, board.Score);
        if (!frame.IsReliable) throw new InvalidOperationException("Board recognition is uncertain.");
        using var engine = new EngineService(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FishEyes", "analysis-cache-v1.json"));
        const int depth = 12;
        // Complete network work before creating WinForms controls, which install
        // a synchronization context that requires a message loop for async work.
        var white = engine.AnalyzeAsync(recognition.Position, true, depth).GetAwaiter().GetResult();
        var black = engine.AnalyzeAsync(recognition.Position, false, depth).GetAwaiter().GetResult();
        using var panel = new MainForm(engine);
        static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>()
            .SelectMany(child => new[] { child }.Concat(Descendants(child)));
        var controls = Descendants(panel).ToArray();
        controls.OfType<Label>().Single(c => c.Text == "Analysis paused").Text = "Moves ready";
        controls.OfType<Label>().Single(c => c.Text == "Turn on to find your board").Text = "Updates automatically as you play";
        string Summary(Analysis analysis) => analysis.Move is { } move ? $"{move[..2]} → {move.Substring(2, 2)}" : analysis.Status;
        controls.Single(c => c.AccessibleName?.StartsWith("White:") == true).Text = Summary(white.Value);
        controls.Single(c => c.AccessibleName?.StartsWith("Black:") == true).Text = Summary(black.Value);
        var toggle = controls.OfType<Button>().Single(c => c.AccessibleName == "Turn on analysis");
        // Set the visual state only; do not start a desktop capture loop.
        toggle.GetType().GetProperty("Active")!.SetValue(toggle, true);
        var preview = controls.OfType<BoardPreview>().Single();
        preview.UpdateBoard(frame, true);
        preview.SetMoves(frame, white.Value, black.Value);
        // PerformClick ignores hidden forms. Invoke the same disclosure handler
        // so this export uses the production layout without showing any window.
        typeof(MainForm).GetMethod("TogglePreview", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(panel, null);
        static Bitmap RenderControl(Control control)
        {
            _ = control.Handle;
            var image = new Bitmap(control.Width, control.Height);
            control.DrawToBitmap(image, new Rectangle(System.Drawing.Point.Empty, image.Size));
            // WM_PRINT skips child windows of a hidden parent. Render each real
            // child explicitly, preserving its production bounds and z-order.
            using var graphics = Graphics.FromImage(image);
            foreach (Control child in control.Controls.Cast<Control>().Reverse())
            {
                if (child.Width <= 0 || child.Height <= 0) continue;
                using var childImage = RenderControl(child);
                graphics.DrawImageUnscaled(childImage, child.Left, child.Top);
            }
            return image;
        }
        using var panelImage = RenderControl(panel);
        using var original = new Bitmap(input);
        const int padding = 20;
        int available = original.Width - bounds.Right - padding * 2;
        int panelWidth = Math.Max(panel.Width, available);
        int width = Math.Max(original.Width, bounds.Right + padding * 2 + panelWidth);
        using var output = new Bitmap(width, original.Height);
        using (var graphics = Graphics.FromImage(output))
        {
            graphics.Clear(original.GetPixel(original.Width - 1, original.Height / 2));
            graphics.DrawImageUnscaled(original, 0, 0);
            ArrowOverlay.DrawMoves(graphics, bounds, recognition.WhiteBottom, white.Value, black.Value);
            float scale = Math.Min(panelWidth / (float)panel.Width, (bounds.Height - padding * 2) / (float)panel.Height);
            int height = (int)Math.Round(panel.Height * scale);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(panelImage, new Rectangle(bounds.Right + padding, bounds.Y + (bounds.Height - height) / 2, (int)Math.Round(panel.Width * scale), height));
        }
        string path = Path.Combine(outputDirectory, "board-and-overlay.png");
        output.Save(path, ImageFormat.Png);
        // Hidden forms do not raise FormClosed; dispose the separate arrow HWND.
        foreach (var overlay in Application.OpenForms.OfType<ArrowOverlay>().ToArray()) overlay.Dispose();
        return new { image = path, rendering = "Actual FishEyes controls rendered beside the supplied screenshot", placement = recognition.Position.Placement,
            recognition.Theme, recognition.MinConfidence, recognition.MeanConfidence, depth, white = white.Value, black = black.Value };
    }
    public static object Inspect(string input)
    {
        using var source = Cv2.ImDecode(File.ReadAllBytes(input), ImreadModes.Color);
        var board = new BoardDetector().Detect(source) ?? throw new InvalidOperationException("No board detected.");
        var match = new ThemeRecognizer().Recognize(source, board.Bounds);
        using var recognizer = new PieceRecognizer();
        var result = recognizer.Recognize(source, board.Bounds);
        return new { bounds = board.Bounds.ToString(), board.Score, match.Theme, match.Error, match.Minimum, match.Mean,
            rawPlacement = new ChessPosition(match.Pieces).Placement,
            match.HasAnnotations,
            squares = match.Squares.Select((s, i) => new { square = $"{(char)('a' + i % 8)}{8 - i / 8}", s.Piece, s.Error, s.Margin, s.Quality, s.Background, s.MaskedFraction }),
            result = new { result.Position.Placement, result.Theme, result.MinConfidence, result.MeanConfidence } };
    }
    public static async Task<object> AnalyzeAsync(string input, string outputDirectory)
    {
        using var source = Cv2.ImDecode(File.ReadAllBytes(input), ImreadModes.Color);
        var detection = new BoardDetector().Detect(source)
            ?? throw new InvalidOperationException("No chessboard detected in the image.");
        using var recognizer = new PieceRecognizer();
        var recognition = recognizer.Recognize(source, detection.Bounds);
        if (recognition.MinConfidence < .60 || recognition.MeanConfidence < .95)
            throw new InvalidOperationException($"Recognition confidence is too low: {recognition.MinConfidence:P1} minimum.");
        if (recognition.Position.InvalidReason(true) is not null && recognition.Position.InvalidReason(false) is not null)
            throw new InvalidOperationException("The recognized position is invalid for both sides.");

        using var engine = new EngineService(null);
        const int depth = 12;
        var white = await engine.AnalyzeAsync(recognition.Position, true, depth);
        var black = await engine.AnalyzeAsync(recognition.Position, false, depth);
        string output = Path.Combine(outputDirectory, "move-arrows.png");
        using var bitmap = new Bitmap(input);
        var box = detection.Bounds;
        using (var graphics = Graphics.FromImage(bitmap))
            ArrowOverlay.DrawMoves(graphics, new Rectangle(box.X, box.Y, box.Width, box.Height), recognition.WhiteBottom, white.Value, black.Value);
        bitmap.Save(output, ImageFormat.Png);
        return new
        {
            placement = recognition.Position.Placement,
            recognition.WhiteBottom, recognition.MinConfidence, recognition.MeanConfidence, recognition.Theme,
            depth, white = white.Value, black = black.Value, image = output
        };
    }
}
