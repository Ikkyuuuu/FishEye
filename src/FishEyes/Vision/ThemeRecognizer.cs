using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace FishEyes;

/// <summary>Matches a single consistent Chess.com sprite set across the board.</summary>
public sealed class ThemeRecognizer
{
    private const int Size = 40;
    private const string Pieces = "PNBRQKpnbrqk";
    private sealed record Theme(string Name, byte[][] Sprites);
    public sealed record SquareMatch(char Piece, double Error, double Margin, float Quality, int[] Background)
    {
        public double MaskedFraction { get; init; }
    }
    public sealed record Match(char[] Pieces, float Minimum, float Mean, string Theme, double Error)
    {
        public SquareMatch[] Squares { get; init; } = [];
        public bool HasAnnotations { get; init; }
    }
    private sealed record Tile(byte[] Pixels, int[] Background, byte[] Mask, double MaskedFraction);
    private static readonly Lazy<Theme[]> Themes = new(Load);
    private Theme? lastTheme;
    public static IReadOnlyList<string> Names => Themes.Value.Select(t => t.Name).ToArray();

    private static Theme[] Load()
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("FishEyes.Themes.br")!;
        using var compressed = new BrotliStream(resource, CompressionMode.Decompress);
        using var reader = new BinaryReader(compressed);
        if (reader.ReadString() != "FishEyesThemes1" || reader.ReadInt32() != Size) throw new InvalidDataException("Invalid theme pack.");
        var result = new Theme[reader.ReadInt32()];
        for (int i = 0; i < result.Length; i++)
        {
            string name = reader.ReadString();
            byte[][] sprites = Enumerable.Range(0, 12).Select(_ => reader.ReadBytes(Size * Size * 4)).ToArray();
            if (sprites.Any(s => s.Length != Size * Size * 4)) throw new InvalidDataException("Truncated theme pack.");
            foreach (var sprite in sprites)
            {
                // Match browser-resampled outlines at the same bandwidth as
                // captured tiles. Keep RGB premultiplied while filtering alpha.
                using var sample = new Mat(Size, Size, MatType.CV_8UC4);
                Marshal.Copy(sprite, 0, sample.Data, sprite.Length);
                Cv2.GaussianBlur(sample, sample, new OpenCvSharp.Size(5, 5), 1);
                Marshal.Copy(sample.Data, sprite, 0, sprite.Length);
            }
            result[i] = new(name, sprites);
        }
        return result;
    }

    public Match Recognize(Mat screen, Rect box)
    {
        var tiles = new Tile[64];
        var annotations = AnnotationMask.Detect(screen, box, Size);
        using var resized = new Mat();
        for (int s = 0; s < 64; s++)
        {
            int x = box.X + s % 8 * box.Width / 8, y = box.Y + s / 8 * box.Height / 8;
            int right = box.X + (s % 8 + 1) * box.Width / 8, bottom = box.Y + (s / 8 + 1) * box.Height / 8;
            using var tile = new Mat(screen, new Rect(x, y, right - x, bottom - y));
            Cv2.Resize(tile, resized, new OpenCvSharp.Size(Size, Size), 0, 0, InterpolationFlags.Area);
            // Sharp one-pixel strokes vary with browser zoom and fractional
            // square sizes. Symmetric filtering preserves the piece silhouette.
            Cv2.GaussianBlur(resized, resized, new OpenCvSharp.Size(5, 5), 1);
            var pixels = new byte[Size * Size * 3]; Marshal.Copy(resized.Data, pixels, 0, pixels.Length);
            var mask = new byte[Size * Size];
            int masked = 0;
            for (int py = 0; py < Size; py++) for (int px = 0; px < Size; px++)
            {
                mask[py * Size + px] = annotations[(s / 8 * Size + py) * Size * 8 + s % 8 * Size + px];
                if (px >= 3 && px < Size - 3 && py >= 3 && py < Size - 3 && mask[py * Size + px] != 0) masked++;
            }
            var background = new int[3];
            for (int c = 0; c < 3; c++)
            {
                var corners = new List<int>();
                foreach (int cy in new[] { 3, 36 }) foreach (int cx in new[] { 3, 36 })
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                        if (mask[(cy + dy) * Size + cx + dx] == 0)
                            corners.Add(pixels[((cy + dy) * Size + cx + dx) * 3 + c]);
                corners.Sort(); background[c] = corners.Count == 0 ? 0 : corners[corners.Count / 2];
            }
            tiles[s] = new(pixels, background, mask, masked / (double)((Size - 6) * (Size - 6)));
        }
        if (lastTheme is not null)
        {
            var cached = Evaluate(lastTheme, tiles);
            if (Accepted(cached)) return cached;
        }
        // Coarse whole-board ranking avoids 12 x 64 full-resolution comparisons
        // for every theme. Empty cells participate, but cannot select a theme alone.
        var candidates = Themes.Value.Select(theme => (Theme: theme, Error: tiles.Sum(tile =>
            Math.Min(Error(tile, null, 0, 0, 4), theme.Sprites.Min(sprite => Error(tile, sprite, 0, 0, 4))))))
            .OrderBy(t => t.Error).Take(3).ToArray();
        Match? best = null;
        foreach (var candidate in candidates)
        {
            var match = Evaluate(candidate.Theme, tiles);
            if (best is null || match.Error < best.Error) best = match;
        }
        if (Accepted(best!)) lastTheme = Themes.Value.First(t => t.Name == best!.Theme);
        return best!;
    }

    public static bool Accepted(Match match) => match.Minimum >= .60f && match.Mean >= .95f;

    private static Match Evaluate(Theme theme, Tile[] tiles)
    {
        var pieces = new char[64]; var quality = new float[64]; double total = 0;
        var squares = new SquareMatch[64];
        for (int s = 0; s < 64; s++)
        {
            var errors = new double[13];
            errors[0] = Error(tiles[s], null, 0, 0, 1);
            for (int p = 0; p < 12; p++)
            {
                double best = double.MaxValue;
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                    best = Math.Min(best, Error(tiles[s], theme.Sprites[p], dx, dy, 1));
                errors[p + 1] = best;
            }
            int index = Array.IndexOf(errors, errors.Min());
            double error = errors[index], second = errors.Where((_, i) => i != index).Min(), margin = second - error;
            pieces[s] = index == 0 ? '.' : Pieces[index - 1];
            // A close runner-up or a poor absolute match is rejected, even when
            // the resulting arrangement would happen to be a legal position.
            quality[s] = tiles[s].MaskedFraction > .45 || error > 14 || margin < .35 || margin / (error + 1) < .08
                ? 0 : (float)(1 - error / 255);
            total += error;
            squares[s] = new(pieces[s], error, margin, quality[s], tiles[s].Background) { MaskedFraction = tiles[s].MaskedFraction };
        }
        return new(pieces, quality.Min(), quality.Average(), theme.Name, total / 64)
        { Squares = squares, HasAnnotations = tiles.Any(t => t.MaskedFraction > 0) };
    }

    private static double Error(Tile tile, byte[]? sprite, int dx, int dy, int step)
    {
        long error = 0; int count = 0;
        // Ignore coordinate labels and the seam between adjacent squares.
        for (int y = 3; y < Size - 3; y += step)
            for (int x = 3; x < Size - 3; x += step)
            {
                if (tile.Mask[y * Size + x] != 0) continue;
                int source = ((y - dy) * Size + x - dx) * 4, target = (y * Size + x) * 3;
                int alpha = sprite is null ? 0 : sprite[source + 3];
                for (int c = 0; c < 3; c++)
                {
                    int expected = (sprite is null ? 0 : sprite[source + c]) + (tile.Background[c] * (255 - alpha) + 127) / 255;
                    error += Math.Min(80, Math.Abs(tile.Pixels[target + c] - expected));
                }
                count += 3;
            }
        return count == 0 ? 255 : (double)error / count;
    }
}
