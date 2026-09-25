using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace FishEyes;

public static class StockfishBundle
{
    public const string Version = "19";
    private sealed record Manifest(string Version, string Url, string Sha256, string Executable, string ExecutableSha256);

    public static async Task<string> EnsureInstalledAsync(CancellationToken cancellation)
    {
        var assembly = typeof(StockfishBundle).Assembly;
        using var metadata = assembly.GetManifestResourceStream("FishEyes.Stockfish.json")!;
        var manifest = JsonSerializer.Deserialize<Manifest>(metadata, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FishEyes", "engines", $"stockfish-{manifest.Version}-{manifest.Sha256[..12]}");
        Directory.CreateDirectory(root);
        // Serialize extraction across application instances; an interrupted install can be retried.
        FileStream? installLock = null;
        while (installLock is null)
        {
            cancellation.ThrowIfCancellationRequested();
            try { installLock = new FileStream(Path.Combine(root, "install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(100, cancellation).ConfigureAwait(false); }
        }
        using (installLock)
        {
            string executable = Path.Combine(root, manifest.Executable);
            string marker = Path.Combine(root, "installed.txt");
            if (File.Exists(marker) && File.Exists(executable) &&
                await HashAsync(executable, cancellation) == manifest.ExecutableSha256) return executable;

            using var stream = assembly.GetManifestResourceStream("FishEyes.Stockfish.zip")
                ?? throw new InvalidOperationException("Bundled Stockfish is missing. Rebuild using scripts/build.ps1.");
            string archiveHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation)).ToLowerInvariant();
            if (archiveHash != manifest.Sha256) throw new InvalidDataException("Bundled Stockfish checksum mismatch.");
            stream.Position = 0;
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                cancellation.ThrowIfCancellationRequested();
                if (entry.Name.Length == 0) continue;
                string destination = Path.GetFullPath(Path.Combine(root, entry.FullName));
                if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Invalid Stockfish archive path.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                string temporary = destination + ".tmp";
                try
                {
                    using (var input = entry.Open())
                    using (var output = File.Create(temporary))
                        await input.CopyToAsync(output, cancellation).ConfigureAwait(false);
                    File.Move(temporary, destination, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            if (await HashAsync(executable, cancellation) != manifest.ExecutableSha256)
                throw new InvalidDataException("Extracted Stockfish checksum mismatch.");
            File.WriteAllText(Path.Combine(root, "source.txt"),
                $"Stockfish {manifest.Version}, GPL-3.0. Exact upstream distribution: {manifest.Url}\n" +
                "License, source and build instructions are included in stockfish/. Neural networks are embedded in the executable; see the upstream Makefile for network downloads.\n");
            File.WriteAllText(marker, manifest.Sha256);
            return executable;
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellation)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellation)).ToLowerInvariant();
    }
}
