using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using OpenCvSharp;

namespace FishEyes;

internal static class ThemePack
{
    internal static readonly string[] Keys = ["whitePawn", "whiteKnight", "whiteBishop", "whiteRook", "whiteQueen", "whiteKing", "blackPawn", "blackKnight", "blackBishop", "blackRook", "blackQueen", "blackKing"];
    public static object Build(string cache, string output)
    {
        using var catalog = JsonDocument.Parse(File.ReadAllText(Path.Combine(cache, "catalog.json")));
        var included = new List<(string Name, byte[][] Images)>();
        var manifest = new List<object>();
        foreach (var set in catalog.RootElement.GetProperty("pieceSets").EnumerateArray())
        {
            string name = set.GetProperty("name").GetString()!, id = set.GetProperty("id").GetString()!;
            string? excluded = name == "Blindfold" ? "Invisible pieces" : name.StartsWith("Checkers") ? "Checkers artwork does not identify chess piece types" : set.GetProperty("perspective").GetString() != "PIECE_PERSPECTIVE_TOP_DOWN" ? "Perspective/overlapping 3D pieces are not supported" : null;
            var images = new List<byte[]>();
            var sources = new List<object>();
            foreach (string key in Keys)
            {
                string path = Path.Combine(cache, id, key + ".png");
                byte[] original = File.ReadAllBytes(path);
                sources.Add(new { piece = key, url = set.GetProperty("images").GetProperty(key).GetString(), sha256 = Convert.ToHexStringLower(SHA256.HashData(original)) });
                if (excluded is not null) continue;
                using var source = Cv2.ImDecode(original, ImreadModes.Unchanged);
                if (source.Empty() || source.Channels() != 4) throw new InvalidDataException($"Invalid RGBA sprite: {name}/{key}");
                // Premultiply before resizing so transparent RGB cannot make dark fringes.
                var bytes = new byte[source.Rows * source.Cols * 4];
                Marshal.Copy(source.Data, bytes, 0, bytes.Length);
                for (int p = 0; p < bytes.Length; p += 4)
                    for (int c = 0; c < 3; c++) bytes[p + c] = (byte)((bytes[p + c] * bytes[p + 3] + 127) / 255);
                Marshal.Copy(bytes, 0, source.Data, bytes.Length);
                using var small = new Mat();
                Cv2.Resize(source, small, new OpenCvSharp.Size(40, 40), 0, 0, InterpolationFlags.Area);
                byte[] pixels = new byte[40 * 40 * 4];
                Marshal.Copy(small.Data, pixels, 0, pixels.Length);
                images.Add(pixels);
            }
            if (excluded is null)
            {
                if (images.Select(Convert.ToHexString).Distinct().Count() != 12) excluded = "Duplicate piece artwork";
                else included.Add((name, images.ToArray()));
            }
            manifest.Add(new { id, name, included = excluded is null, reason = excluded, sources });
        }
        string pack = Path.Combine(output, "chesscom-templates.br");
        using (var file = File.Create(pack))
        using (var compressed = new BrotliStream(file, CompressionLevel.SmallestSize))
        using (var writer = new BinaryWriter(compressed))
        {
            writer.Write("FishEyesThemes1"); writer.Write(40); writer.Write(included.Count);
            foreach (var set in included) { writer.Write(set.Name); foreach (byte[] pixels in set.Images) writer.Write(pixels); }
        }
        File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(new { catalog = "https://www.chess.com/rpc/chesscom.themes.v2.ThemesService/ListAllThemeElements", retrieved = DateTime.UtcNow.ToString("yyyy-MM-dd"), sets = manifest }, new JsonSerializerOptions { WriteIndented = true }));
        return new { included = included.Count, excluded = manifest.Count - included.Count, bytes = new FileInfo(pack).Length };
    }
}
