using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenCvSharp;

namespace FishEyes;

internal static class ThemeTests
{
    private const string Start = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR";
    private const string Middle = "r5k1/1R3bp1/3p3p/2q2p2/p1P1pP2/P3P1P1/1Q1N1K1P/8";
    public static object Run(string cache, string output)
    {
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(cache, "catalog.json")));
        var results = new List<object>(); var failures = new List<string>();
        var matcher = new ThemeRecognizer();
        var detector = new BoardDetector();
        var timings = new List<double>();
        foreach (var set in catalog.RootElement.GetProperty("pieceSets").EnumerateArray())
        {
            string name = set.GetProperty("name").GetString()!;
            if (!ThemeRecognizer.Names.Contains(name)) continue;
            foreach (bool flip in new[] { false, true })
            {
                string expected = flip ? Middle : Start;
                int cell = flip ? 87 : 56;
                using var board = Render(Path.Combine(cache, set.GetProperty("id").GetString()!), expected, cell, flip);
                using var screen = new Mat(board.Height + 91, board.Width + 143, MatType.CV_8UC3, new Scalar(29, 32, 37));
                using (var target = new Mat(screen, new Rect(61, 37, board.Width, board.Height))) board.CopyTo(target);
                var detected = detector.Detect(screen);
                if (detected is null) { failures.Add($"{name} (flip={flip}): grid not found"); continue; }
                var watch = Stopwatch.StartNew();
                var match = matcher.Recognize(screen, detected.Bounds);
                timings.Add(watch.Elapsed.TotalMilliseconds);
                var squares = (char[])match.Pieces.Clone(); if (flip) Array.Reverse(squares);
                string placement = new ChessPosition(squares).Placement;
                bool passed = placement == expected && ThemeRecognizer.Accepted(match);
                results.Add(new { name, flip, passed, placement, bounds = detected.Bounds.ToString(), match.Theme, match.Minimum, match.Mean, match.Error, milliseconds = watch.Elapsed.TotalMilliseconds });
                if (!passed)
                {
                    failures.Add($"{name} (flip={flip}): {placement} [{match.Minimum:F3}/{match.Mean:F3}]");
                    Cv2.ImEncode(".png", screen, out byte[] image);
                    File.WriteAllBytes(Path.Combine(output, set.GetProperty("id").GetString() + (flip ? "-flipped" : "") + ".png"), image);
                }
            }
        }
        File.WriteAllText(Path.Combine(output, "theme-results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        if (failures.Count != 0) throw new InvalidOperationException(string.Join("\n", failures));
        return new { passed = true, themes = results.Count / 2, scenarios = results.Count, averageMilliseconds = timings.Average(), maximumMilliseconds = timings.Max() };
    }

    private static Mat Render(string directory, string placement, int cell, bool flip)
    {
        int size = cell * 8;
        var pixels = new byte[size * size * 3];
        var random = new Random(174);
        char[] squares = ChessPosition.Parse(placement).Squares.ToArray(); if (flip) Array.Reverse(squares);
        for (int s = 0; s < 64; s++)
        {
            bool light = (s / 8 + s % 8) % 2 == 0;
            int[] bg = flip ? (light ? [216, 232, 238] : [139, 119, 96]) : (light ? [210, 237, 238] : [81, 149, 121]);
            if (flip && s is 10 or 26) bg = light ? [100, 226, 241] : [59, 181, 197];
            byte[]? sprite = null;
            int piece = "PNBRQKpnbrqk".IndexOf(squares[s]);
            if (piece >= 0)
            {
                using var original = Cv2.ImDecode(File.ReadAllBytes(Path.Combine(directory, ThemePack.Keys[piece] + ".png")), ImreadModes.Unchanged);
                using var resized = new Mat();
                Cv2.Resize(original, resized, new OpenCvSharp.Size(cell, cell), 0, 0, InterpolationFlags.Linear);
                sprite = new byte[cell * cell * 4]; Marshal.Copy(resized.Data, sprite, 0, sprite.Length);
            }
            for (int y = 0; y < cell; y++) for (int x = 0; x < cell; x++)
            {
                int p = (y * cell + x) * 4, t = ((s / 8 * cell + y) * size + s % 8 * cell + x) * 3;
                int alpha = sprite is null ? 0 : sprite[p + 3];
                int texture = flip ? random.Next(-18, 19) : 0;
                for (int c = 0; c < 3; c++) pixels[t + c] = (byte)(((sprite is null ? 0 : sprite[p + c] * alpha) + Math.Clamp(bg[c] + texture, 0, 255) * (255 - alpha) + 127) / 255);
            }
        }
        var result = new Mat(size, size, MatType.CV_8UC3); Marshal.Copy(pixels, 0, result.Data, pixels.Length);
        for (int i = 0; i < 8; i++)
        {
            Cv2.PutText(result, (8 - i).ToString(), new OpenCvSharp.Point(2, i * cell + 12), HersheyFonts.HersheySimplex, .35, Scalar.White, 1);
            Cv2.PutText(result, ((char)('a' + i)).ToString(), new OpenCvSharp.Point((i + 1) * cell - 9, size - 3), HersheyFonts.HersheySimplex, .35, Scalar.White, 1);
        }
        return result;
    }
}
