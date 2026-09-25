using System.Runtime.InteropServices;
using OpenCvSharp;

namespace FishEyes;

/// <summary>Finds colored annotation strokes that continue across square boundaries.</summary>
internal static class AnnotationMask
{
    public static byte[] Detect(Mat screen, Rect bounds, int cell)
    {
        int size = cell * 8;
        using var board = new Mat(screen, bounds);
        using var resized = new Mat();
        using var hsv = new Mat();
        Cv2.Resize(board, resized, new OpenCvSharp.Size(size, size), 0, 0, InterpolationFlags.Area);
        Cv2.CvtColor(resized, hsv, ColorConversionCodes.BGR2HSV);
        var colors = new byte[size * size * 3];
        Marshal.Copy(hsv.Data, colors, 0, colors.Length);
        var pixels = new byte[colors.Length];
        Marshal.Copy(resized.Data, pixels, 0, pixels.Length);
        var backgrounds = new int[64, 3];
        for (int s = 0; s < 64; s++) for (int c = 0; c < 3; c++)
        {
            var samples = new List<int>();
            foreach (int cy in new[] { 3, cell - 4 }) foreach (int cx in new[] { 3, cell - 4 })
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                    samples.Add(pixels[((s / 8 * cell + cy + dy) * size + s % 8 * cell + cx + dx) * 3 + c]);
            samples.Sort(); backgrounds[s, c] = samples[samples.Count / 2];
        }
        var foreground = new bool[size * size];
        for (int i = 0; i < foreground.Length; i++)
        {
            int square = i / size / cell * 8 + i % size / cell, distance = 0;
            for (int c = 0; c < 3; c++)
            {
                int delta = pixels[i * 3 + c] - backgrounds[square, c];
                distance += delta * delta;
            }
            // Orange/brown or green board themes must not join a stroke to an
            // entire background component just because their hues match.
            foreground[i] = distance >= 55 * 55;
        }
        var result = new byte[size * size];
        var selected = new byte[result.Length];
        using var mask = new Mat(size, size, MatType.CV_8UC1);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var ids = new int[result.Length];

        // Overlapping hue bands include orange, red, green and blue annotations.
        // Color alone is insufficient: many piece skins have the same colors.
        for (int hue = 0; hue < 180; hue += 15)
        {
            for (int i = 0; i < selected.Length; i++)
            {
                int distance = Math.Abs(colors[i * 3] - hue);
                distance = Math.Min(distance, 180 - distance);
                selected[i] = foreground[i] && distance <= 10 && colors[i * 3 + 1] >= 130 && colors[i * 3 + 2] >= 170 ? (byte)255 : (byte)0;
            }
            Marshal.Copy(selected, 0, mask.Data, selected.Length);
            int count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids, PixelConnectivity.Connectivity8);
            Marshal.Copy(labels.Data, ids, 0, ids.Length);
            var eligible = new bool[count];
            for (int id = 1; id < count; id++)
            {
                int width = stats.At<int>(id, (int)ConnectedComponentsTypes.Width);
                int height = stats.At<int>(id, (int)ConnectedComponentsTypes.Height);
                int area = stats.At<int>(id, (int)ConnectedComponentsTypes.Area);
                // Pieces stay inside a square. A stroke must span more than one,
                // with enough pixels to rule out texture and coordinate labels.
                eligible[id] = Math.Max(width, height) >= cell * 1.25 && area >= cell * cell * .12;
            }
            var squareAreas = new int[count, 64];
            for (int i = 0; i < ids.Length; i++)
                if (eligible[ids[i]]) squareAreas[ids[i], i / size / cell * 8 + i % size / cell]++;
            for (int id = 1; id < count; id++)
            {
                if (!eligible[id]) continue;
                int occupied = 0, area = 0;
                for (int s = 0; s < 64; s++)
                {
                    area += squareAreas[id, s];
                    if (squareAreas[id, s] >= cell * cell * .04) occupied++;
                }
                // Broad colored squares / backgrounds are not annotation lines.
                eligible[id] = occupied >= 2 && area < occupied * cell * cell * .45;
            }
            for (int i = 0; i < ids.Length; i++)
                if (eligible[ids[i]]) result[i] = 255;
        }
        // Cover antialiased edges and the footprint of the matching blur.
        Marshal.Copy(result, 0, mask.Data, result.Length);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
        Cv2.Dilate(mask, mask, kernel);
        Marshal.Copy(mask.Data, result, 0, result.Length);
        return result;
    }
}
