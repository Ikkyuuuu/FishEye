# Development notes

Run the commands below from the repository root.

## Build from source

Requirements: Windows x64, the **.NET 10 SDK**, and PowerShell. Run from the repository root:

```powershell
# Run the source application
dotnet run --project src/FishEyes

# Run deterministic tests and publish the standalone executable
.\scripts\build.ps1 -Test
```

Open `FishEyes.slnx` in an IDE that supports .NET 10, or use `dotnet build FishEyes.slnx`.

## Tests

Tests live in a separate executable project and are not bundled in the application. The runner exits with a nonzero status on failure; use the scripts below rather than `dotnet test`.

```powershell
# Deterministic tests; no engine network calls
.\scripts\test.ps1

# Optional integration test against the real Stockfish API
.\scripts\test.ps1 -LiveApi

# Optional interactive desktop test; temporarily opens a sample board
.\scripts\test.ps1 -Gui
```

Tests cover board detection, recognition, orientation, move legality, caching, request deduplication, cancellation, and retry backoff. The GUI test additionally verifies live screen capture, both arrows, click-through behavior, and pausing. Reports and generated test images go in `artifacts/test-results/`.

GitHub Actions runs the deterministic tests and publishes a downloadable Windows build on pushes and pull requests. Interactive GUI tests and live API calls are opt-in local checks.

### Analyze an image

To run board detection, recognition, and real engine analysis on another image:

```powershell
dotnet run --project tests/FishEyes.Tests -c Release -- --analyze-image --sample "path/to/board.png" --output artifacts/image-example/analysis.json
```

This uses depth 12 and saves a recognition/analysis report plus `move-arrows.png` beside the report. It uses the application's own arrow renderer and checks recognition confidence and move legality before producing the image.

## Project structure

```text
FishEyes.slnx
src/FishEyes/
  Core/             Chess positions and move validation
  Services/         Screen capture and cached engine requests
  Vision/           Grid detection and ONNX piece recognition
  UI/               Control panel and arrow overlay
  Program.cs        Application entry point
tests/FishEyes.Tests/
  Fixtures/         Sample board used by tests
assets/models/      Bundled ONNX model
scripts/            Build and test entry points
docs/               User guide and screenshots
licenses/           Third-party notices
.github/workflows/  Windows CI build
```

Generated binaries, IDE files, and test reports are ignored by Git. Publish executables through GitHub Releases or Actions artifacts; commit the source, model, fixture, and documentation.

## Capture, API, and cache

Screen images are processed in memory and are not saved during normal use. Only the recognized FEN and requested depth are sent to [StockfishOnline](https://stockfish.online/docs.php). An internet connection is needed for uncached analysis.

The cache is stored at `%LOCALAPPDATA%\FishEyes\analysis-cache-v1.json`. Completed results survive restarts. Unchanged boards and previously analyzed positions reuse those results at the same depth. Concurrent requests share work; transient failures back off from 15 seconds to 5 minutes.

Screens continue updating while the engine is working. Stale results cannot replace the current arrows. FishEyes excludes its own windows from capture, with a brief hide-and-capture fallback when Windows cannot exclude them.

## Limitations

- Requires an axis-aligned 2D board, entirely visible on one monitor. If multiple boards are visible, the largest confidently recognized board is selected.
- Unfamiliar piece themes, low contrast, animations, or very small boards can prevent recognition. Low-confidence and invalid positions are skipped.
- Orientation is inferred from piece placement and maintained across nearby positions. Starting directly in an unusual endgame may infer it incorrectly.
- Castling and en passant rights cannot be established from screenshots and are assumed unavailable. Repetition and the fifty-move rule are not tracked.
- Capture targets one frame per second; a slow recognition pass delays the next scan.

## Dependencies and licensing

FishEyes uses .NET / Windows Forms, ONNX Runtime, OpenCvSharp / OpenCV, and the [chessvision recognition model](https://huggingface.co/harshitpawar64/chessvision) by Harshit Pawar. See [third-party notices](../licenses/README.txt) and [model information](../assets/models/README.md). Stockfish itself is not bundled; analysis uses the external HTTP service.

No license has been selected for the original FishEyes application code. The files under `licenses/` describe third-party components and do not license the application as a whole.
