using System.Drawing.Imaging;
using OpenCvSharp;

namespace FishEyes;

internal static class ImageExample
{
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
            recognition.WhiteBottom, recognition.MinConfidence, recognition.MeanConfidence,
            depth, white = white.Value, black = black.Value, image = output
        };
    }
}
