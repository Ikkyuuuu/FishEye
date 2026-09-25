# Bundled Stockfish

FishEyes embeds the unmodified official Stockfish 19 Windows x86-64 universal ZIP, including its source, build documentation, authors, and GPLv3 license. The universal executable selects the CPU implementation automatically.

`stockfish.json` pins the upstream URL and SHA-256 hashes of both the archive and executable. Every build runs `scripts/prepare-stockfish.ps1`, which downloads the archive only if absent or invalid. The downloaded ZIP is ignored by Git. No download happens when using the finished application.

On first analysis, FishEyes verifies and extracts the package into `%LOCALAPPDATA%\FishEyes\engines\stockfish-19-<archive hash prefix>`. Installation is serialized between app instances and interrupted extraction is retried. Later starts verify the extracted executable before launching it without a console. The source and license stay beside it.

Upstream: https://github.com/official-stockfish/Stockfish/releases/tag/sf_19

To update, choose an official release, update both hashes and URL in the manifest, update `StockfishBundle.Version` and the notices, then run the real-engine tests. The engine version and search settings are part of the cache key.
