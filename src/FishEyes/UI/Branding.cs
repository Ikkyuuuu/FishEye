using System.Reflection;

namespace FishEyes;

internal static class Branding
{
    public static readonly Image Logo = LoadLogo();
    public static readonly Icon AppIcon = LoadIcon();

    private static Stream Resource(string name) => Assembly.GetExecutingAssembly()
        .GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Missing branding asset: {name}");

    private static Image LoadLogo()
    {
        using var stream = Resource("FishEyes.Logo.png");
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }
    private static Icon LoadIcon()
    {
        using var stream = Resource("FishEyes.App.ico");
        using var icon = new Icon(stream, new Size(32, 32));
        return (Icon)icon.Clone();
    }
}
