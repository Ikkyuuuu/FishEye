using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using Rect = OpenCvSharp.Rect;

namespace FishEyes;

public record BoardDetection(Rect Bounds, double Score);
public record Recognition(ChessPosition Position, bool WhiteBottom, float MinConfidence, float MeanConfidence);

public sealed class BoardDetector
{
    public BoardDetection? Detect(Mat screen, Rect? previous = null)
    {
        double scale = Math.Min(1, 1600.0 / Math.Max(screen.Width, screen.Height));
        using var small = new Mat();
        Cv2.Resize(screen, small, new OpenCvSharp.Size(), scale, scale, InterpolationFlags.Area);
        byte[] pixels = new byte[small.Rows * small.Cols * 3];
        Marshal.Copy(small.Data, pixels, 0, pixels.Length);
        var candidates = new Dictionary<(int, int, int), Rect>();
        void Add(int x, int y, int size)
        {
            if (size < 128 || x < 0 || y < 0 || x + size > small.Width || y + size > small.Height) return;
            candidates.TryAdd((x / 2, y / 2, size / 2), new Rect(x, y, size, size));
        }
        if (previous is { } old)
        {
            var box = new Rect((int)Math.Round(old.X * scale), (int)Math.Round(old.Y * scale), (int)Math.Round(old.Width * scale), (int)Math.Round(old.Height * scale));
            double score = Score(pixels, small.Width, small.Height, box);
            if (score >= .86) return new(old, score);
            Add(box.X, box.Y, box.Width);
        }
        Add(0, 0, Math.Min(small.Width, small.Height));
        using var gray = new Mat();
        using var mask = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
        Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);
        var tiles = new List<Rect>();
        // Multiple thresholds handle light, dark, and colored board themes.
        for (int pass = 0; pass < 7; pass++)
        {
            if (pass == 0) Cv2.Canny(gray, mask, 35, 100);
            else Cv2.Threshold(gray, mask, 35 + pass * 30, 255, ThresholdTypes.Binary);
            Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);
            Cv2.FindContours(mask, out OpenCvSharp.Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);
            foreach (var contour in contours)
            {
                var rect = Cv2.BoundingRect(contour);
                if (rect.Width < 15 || Math.Abs(rect.Width - rect.Height) > Math.Max(2, rect.Width * .035)) continue;
                Add(rect.X, rect.Y, (rect.Width + rect.Height) / 2);
                double fill = Math.Abs(Cv2.ContourArea(contour)) / (rect.Width * rect.Height);
                if (rect.Width <= Math.Min(small.Width, small.Height) / 8 + 2 && fill > .72)
                    tiles.Add(rect);
            }
        }
        // An empty square is enough to propose the complete 8x8 lattice, even
        // when the board perimeter blends into the surrounding webpage.
        foreach (var tile in tiles.DistinctBy(r => (r.X / 3, r.Y / 3, r.Width / 2)).Take(180))
            foreach (int delta in new[] { 0, -1, 1 })
            {
                int cell = (tile.Width + tile.Height) / 2 + delta;
                for (int row = 0; row < 8; row++)
                    for (int col = 0; col < 8; col++) Add(tile.X - col * cell, tile.Y - row * cell, cell * 8);
            }
        BoardDetection? best = null;
        foreach (var box in candidates.Values)
        {
            double score = Score(pixels, small.Width, small.Height, box);
            if (score < .78) continue;
            if (best is null || score + Math.Log(box.Width) * .025 > best.Score + Math.Log(best.Bounds.Width) * .025)
                best = new(box, score);
        }
        if (best is null) return null;
        var b = best.Bounds;
        var result = new Rect((int)Math.Round(b.X / scale), (int)Math.Round(b.Y / scale), (int)Math.Round(b.Width / scale), (int)Math.Round(b.Height / scale));
        result.Width = Math.Min(result.Width, screen.Width - result.X);
        result.Height = Math.Min(result.Height, screen.Height - result.Y);
        return new(result, best.Score);
    }

    private static double Score(byte[] pixels, int width, int height, Rect box)
    {
        if (box.X < 0 || box.Y < 0 || box.Right > width || box.Bottom > height || box.Width < 100) return 0;
        Span<double> colors = stackalloc double[64 * 3];
        Span<double> sample = stackalloc double[4];
        // Corner samples avoid most of the piece artwork. Per-square medians
        // tolerate coordinates, selection highlights and anti-aliased edges.
        for (int row = 0; row < 8; row++)
            for (int col = 0; col < 8; col++)
                for (int channel = 0; channel < 3; channel++)
                {
                    int i = 0;
                    for (int cornerY = 0; cornerY < 2; cornerY++)
                        for (int cornerX = 0; cornerX < 2; cornerX++)
                        {
                            double dx = cornerX == 0 ? .14 : .86, dy = cornerY == 0 ? .14 : .86;
                            int x = box.X + (int)((col + dx) * box.Width / 8);
                            int y = box.Y + (int)((row + dy) * box.Height / 8);
                            sample[i++] = pixels[(y * width + x) * 3 + channel];
                        }
                    sample.Sort();
                    colors[(row * 8 + col) * 3 + channel] = (sample[1] + sample[2]) / 2;
                }
        Span<double> centers = stackalloc double[6];
        Span<double> group = stackalloc double[32];
        for (int parity = 0; parity < 2; parity++)
            for (int ch = 0; ch < 3; ch++)
            {
                int i = 0;
                for (int s = 0; s < 64; s++)
                    if ((s / 8 + s % 8) % 2 == parity) group[i++] = colors[s * 3 + ch];
                group.Sort(); centers[parity * 3 + ch] = (group[15] + group[16]) / 2;
            }
        double contrast = 0;
        for (int ch = 0; ch < 3; ch++) contrast += Math.Pow(centers[ch] - centers[3 + ch], 2);
        contrast = Math.Sqrt(contrast);
        if (contrast < 28) return 0;
        int matching = 0;
        double error = 0;
        Span<int> rows = stackalloc int[8];
        Span<int> cols = stackalloc int[8];
        rows.Clear(); cols.Clear();
        for (int s = 0; s < 64; s++)
        {
            int parity = (s / 8 + s % 8) % 2;
            double distance = 0;
            for (int ch = 0; ch < 3; ch++) distance += Math.Pow(colors[s * 3 + ch] - centers[parity * 3 + ch], 2);
            distance = Math.Sqrt(distance);
            if (distance < Math.Max(24, contrast * .36)) { matching++; rows[s / 8]++; cols[s % 8]++; }
            error += Math.Min(distance / contrast, 1);
        }
        for (int i = 0; i < 8; i++) if (rows[i] < 4 || cols[i] < 4) return 0;
        if (matching < 49) return 0;
        // Verify that color changes actually fall on all seven grid lines.
        // Checker colors alone also fit slightly oversized/off-center crops.
        double vertical = 0, horizontal = 0;
        int step = Math.Max(1, box.Width / 300);
        for (int line = 1; line < 8; line++)
            for (int cell = 0; cell < 8; cell++)
                for (int corner = 0; corner < 2; corner++)
                {
                    double offset = corner == 0 ? .15 : .85;
                    int xLine = box.X + line * box.Width / 8;
                    int yLine = box.Y + line * box.Height / 8;
                    int xSample = box.X + (int)((cell + offset) * box.Width / 8);
                    int ySample = box.Y + (int)((cell + offset) * box.Height / 8);
                    double dx = 0, dy = 0;
                    for (int c = 0; c < 3; c++)
                    {
                        dx += Math.Pow(pixels[(ySample * width + xLine - step) * 3 + c] - pixels[(ySample * width + xLine + step) * 3 + c], 2);
                        dy += Math.Pow(pixels[((yLine - step) * width + xSample) * 3 + c] - pixels[((yLine + step) * width + xSample) * 3 + c], 2);
                    }
                    vertical += Math.Min(1, Math.Sqrt(dx) / contrast);
                    horizontal += Math.Min(1, Math.Sqrt(dy) / contrast);
                }
        double alignment = Math.Min(vertical, horizontal) / 112;
        if (alignment < .55) return 0;
        return matching / 64.0 * .70 + (1 - error / 64) * .10 + alignment * .20;
    }
}

public sealed class PieceRecognizer : IDisposable
{
    private readonly InferenceSession session;
    private readonly int size;
    private readonly float[] mean, std;
    private readonly char[] classes;
    public PieceRecognizer()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("FishEyes.Model.onnx")!;
        using var data = new MemoryStream(); stream.CopyTo(data);
        using var options = new SessionOptions { IntraOpNumThreads = Math.Min(4, Environment.ProcessorCount), InterOpNumThreads = 1 };
        session = new InferenceSession(data.ToArray(), options);
        var metadata = session.ModelMetadata.CustomMetadataMap;
        size = int.Parse(metadata["image_size"], CultureInfo.InvariantCulture);
        mean = metadata["mean"].Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        std = metadata["std"].Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        classes = metadata["classes"].Split(',').Select(c => c == "empty" ? '.' : c[0] == 'w' ? c[1] : char.ToLowerInvariant(c[1])).ToArray();
    }
    public Recognition Recognize(Mat screen, Rect box, Recognition? previous = null)
    {
        var tensor = new DenseTensor<float>(new[] { 64, 3, size, size });
        byte[] pixels = new byte[size * size * 3];
        using var resized = new Mat();
        for (int row = 0; row < 8; row++)
            for (int col = 0; col < 8; col++)
            {
                int x = box.X + col * box.Width / 8, y = box.Y + row * box.Height / 8;
                int right = box.X + (col + 1) * box.Width / 8, bottom = box.Y + (row + 1) * box.Height / 8;
                using var tile = new Mat(screen, new Rect(x, y, right - x, bottom - y));
                Cv2.Resize(tile, resized, new OpenCvSharp.Size(size, size), 0, 0, InterpolationFlags.Cubic);
                Marshal.Copy(resized.Data, pixels, 0, pixels.Length);
                int square = row * 8 + col;
                for (int py = 0; py < size; py++)
                    for (int px = 0; px < size; px++)
                        for (int c = 0; c < 3; c++) tensor[square, c, py, px] = (pixels[(py * size + px) * 3 + 2 - c] / 255f - mean[c]) / std[c];
            }
        using var outputs = session.Run(new[] { NamedOnnxValue.CreateFromTensor(session.InputNames[0], tensor) });
        var logits = outputs.First().AsTensor<float>().ToArray();
        var pieces = new char[64];
        var confidences = new float[64];
        for (int i = 0; i < 64; i++)
        {
            int start = i * classes.Length, best = 0;
            for (int j = 1; j < classes.Length; j++) if (logits[start + j] > logits[start + best]) best = j;
            double sum = 0;
            for (int j = 0; j < classes.Length; j++) sum += Math.Exp(logits[start + j] - logits[start + best]);
            pieces[i] = classes[best]; confidences[i] = (float)(1 / sum);
        }
        double whiteRow = Enumerable.Range(0, 64).Where(i => char.IsUpper(pieces[i])).Select(i => (double)(i / 8)).DefaultIfEmpty(5).Average();
        double blackRow = Enumerable.Range(0, 64).Where(i => char.IsLower(pieces[i])).Select(i => (double)(i / 8)).DefaultIfEmpty(2).Average();
        bool whiteBottom = whiteRow >= blackRow;
        if (previous is not null)
        {
            int normal = Enumerable.Range(0, 64).Count(i => pieces[i] != previous.Position.Squares[i]);
            int flipped = Enumerable.Range(0, 64).Count(i => pieces[63 - i] != previous.Position.Squares[i]);
            if (Math.Min(normal, flipped) <= 6) whiteBottom = normal <= flipped;
        }
        if (!whiteBottom) Array.Reverse(pieces);
        return new(new ChessPosition(pieces), whiteBottom, confidences.Min(), confidences.Average());
    }
    public void Dispose() => session.Dispose();
}
