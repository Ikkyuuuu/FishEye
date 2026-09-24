using System.Reflection;

namespace FishEyes;

internal static class PreviewPieces
{
    private static readonly Lazy<IReadOnlyDictionary<char, Image>> Sprites = new(Load);
    public static Image? For(char piece) => Sprites.Value.GetValueOrDefault(piece);

    private static IReadOnlyDictionary<char, Image> Load()
    {
        var result = new Dictionary<char, Image>();
        foreach (char piece in "PNBRQKpnbrqk")
        {
            string file = $"{(char.IsUpper(piece) ? 'w' : 'b')}{char.ToLowerInvariant(piece)}";
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"FishEyes.Pieces.{file}.png")
                ?? throw new InvalidOperationException($"Missing preview sprite: {file}");
            using var source = Image.FromStream(stream);
            result[piece] = new Bitmap(source);
        }
        return result;
    }
}
