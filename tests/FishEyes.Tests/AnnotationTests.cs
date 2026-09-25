using OpenCvSharp;

namespace FishEyes;

internal static class AnnotationTests
{
    public static void Run(string output)
    {
        const string start = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR";
        const string planned = "Nn3bnr/pp2k1pp/4b3/5p2/3P4/8/PPP1BPPP/R1B1K1NR";
        var detector = new BoardDetector();
        using var recognizer = new PieceRecognizer();
        Recognition? previous = null;
        void Verify(Mat image, string expected, string name)
        {
            var board = detector.Detect(image) ?? throw new InvalidOperationException($"{name}: grid missing");
            var result = recognizer.Recognize(image, board.Bounds, previous);
            if (result.Position.Placement != expected || result.MinConfidence < .60 || result.MeanConfidence < .95)
            {
                Cv2.ImEncode(".png", image, out var bytes);
                File.WriteAllBytes(Path.Combine(output, "annotation-failure.png"), bytes);
                throw new InvalidOperationException($"{name}: {result.Position.Placement} [{result.MinConfidence:F3}/{result.MeanConfidence:F3}]");
            }
            previous = result;
            var tracked = detector.Detect(image, board.Bounds);
            if (tracked is null || Math.Abs(tracked.Bounds.X - board.Bounds.X) > 2 || Math.Abs(tracked.Bounds.Y - board.Bounds.Y) > 2)
                throw new InvalidOperationException($"{name}: unstable tracked bounds");
        }
        using var fixture = Cv2.ImDecode(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "planning-arrow.png")), ImreadModes.Color);
        foreach (double scale in new[] { 1.0, .7, .5 })
        {
            using var resized = new Mat();
            Cv2.Resize(fixture, resized, new OpenCvSharp.Size(), scale, scale, InterpolationFlags.Area);
            // A fresh recognizer must work even if the first capture has arrows.
            var board = detector.Detect(resized)!;
            using var fresh = new PieceRecognizer();
            var cold = fresh.Recognize(resized, board.Bounds);
            if (cold.Position.Placement != planned || cold.MinConfidence < .60 || cold.Theme != "Band Class")
                throw new InvalidOperationException($"Planning screenshot cold start at {scale}: {cold.Position.Placement} [{cold.MinConfidence:F3}]");
            Verify(resized, planned, $"Planning screenshot at {scale}");
        }

        using var clean = RenderNeo(start);
        foreach (var color in new[] { new Scalar(0, 165, 255), new Scalar(60, 210, 70), new Scalar(50, 60, 235), new Scalar(230, 140, 40) })
            foreach (string shape in new[] { "vertical", "diagonal", "knight", "multiple" })
            {
                using var annotated = Draw(clean, color, shape);
                Verify(annotated, start, $"{color} {shape}");
                Verify(clean, start, "Annotation removed");
            }

        // A real move underneath an unchanged annotation must still be noticed.
        using var moved = clean.Clone();
        using (var from = new Mat(clean, new Rect(320, 480, 80, 80)))
        using (var to = new Mat(moved, new Rect(320, 320, 80, 80))) from.CopyTo(to);
        using (var empty = new Mat(clean, new Rect(160, 320, 80, 80)))
        using (var from = new Mat(moved, new Rect(320, 480, 80, 80))) empty.CopyTo(from);
        using (var annotated = Draw(moved, new Scalar(0, 165, 255), "knight"))
            Verify(annotated, "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR", "Move while annotation remains");

        using (var flipped = RenderNeo(start, flip: true))
        using (var annotated = Draw(flipped, new Scalar(230, 140, 40), "knight"))
        {
            Verify(annotated, start, "Planning arrow with Black at the bottom");
            if (previous!.WhiteBottom) throw new InvalidOperationException("Annotated board orientation is reversed");
        }

        // When too much of a square is hidden, do not let the unmasked neural
        // fallback turn an uncertain template result into an accepted position.
        using var covered = clean.Clone();
        Cv2.Line(covered, new OpenCvSharp.Point(280, 140), new OpenCvSharp.Point(280, 500), new Scalar(0, 165, 255), 23);
        Cv2.Line(covered, new OpenCvSharp.Point(120, 280), new OpenCvSharp.Point(520, 280), new Scalar(0, 165, 255), 23);
        var detection = detector.Detect(covered)!;
        var uncertain = recognizer.Recognize(covered, detection.Bounds);
        if (uncertain.MinConfidence >= .60 && uncertain.MeanConfidence >= .95)
            throw new InvalidOperationException("Heavy annotation coverage should be rejected");
    }

    private static Mat RenderNeo(string placement, bool flip = false)
    {
        using var bitmap = new Bitmap(640, 640);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var pieces = ChessPosition.Parse(placement).Squares.ToArray();
            if (flip) Array.Reverse(pieces);
            for (int s = 0; s < 64; s++)
            {
                var bounds = new Rectangle(s % 8 * 80, s / 8 * 80, 80, 80);
                // Deliberately use orange/brown squares: similar hue to the ink.
                using var brush = new SolidBrush((s / 8 + s % 8) % 2 == 0 ? Color.FromArgb(255, 206, 158) : Color.FromArgb(209, 139, 71));
                graphics.FillRectangle(brush, bounds);
                char piece = pieces[s];
                if (piece == '.') continue;
                string name = (char.IsUpper(piece) ? "w" : "b") + char.ToLowerInvariant(piece);
                using var stream = typeof(PieceRecognizer).Assembly.GetManifestResourceStream($"FishEyes.Pieces.{name}.png")!;
                using var sprite = Image.FromStream(stream);
                graphics.DrawImage(sprite, bounds);
            }
        }
        return ScreenCapture.FromBitmap(bitmap);
    }

    private static Mat Draw(Mat clean, Scalar color, string shape)
    {
        using var ink = clean.Clone();
        var from = new OpenCvSharp.Point(120, 520);
        var to = shape == "vertical" ? new OpenCvSharp.Point(120, 280) : new OpenCvSharp.Point(360, 280);
        if (shape == "knight")
        {
            var elbow = new OpenCvSharp.Point(120, 360);
            Cv2.Line(ink, from, elbow, color, 13, LineTypes.AntiAlias);
            Cv2.ArrowedLine(ink, elbow, new OpenCvSharp.Point(200, 360), color, 13, LineTypes.AntiAlias, 0, .3);
        }
        else
        {
            Cv2.ArrowedLine(ink, from, to, color, 13, LineTypes.AntiAlias, 0, .12);
            if (shape == "multiple")
                Cv2.ArrowedLine(ink, new OpenCvSharp.Point(520, 520), new OpenCvSharp.Point(520, 280), color, 13, LineTypes.AntiAlias, 0, .12);
        }
        var result = new Mat();
        Cv2.AddWeighted(ink, .82, clean, .18, 0, result);
        return result;
    }
}
