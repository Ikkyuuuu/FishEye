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

To verify the published executable's embedded engine without opening the overlay, run `FishEyes.exe --engine-check <report.json>`. This hashes the embedded archive, analyzes both sides of the starting position locally, verifies cache reuse, writes a JSON report and exits (nonzero on failure).

## Temporary window mode

For README screenshots, close the existing instance and double-click `FishEyes-Window.cmd` beside the executable. It runs `./FishEyes.exe --window` (from source: `dotnet run --project src/FishEyes -- --window`). This opens a normal, capturable window with a title bar. It is not always on top, and suggested moves appear only in its board preview. The scanner masks this window inside its own recognition image so the preview cannot become a second input board; screenshot tools still see it normally. Keep the actual board uncovered while analysis is running.

This option is not saved. Launch without `--window` to return to the normal overlay.

## Tests

Tests live in a separate executable project and are not bundled in the application. The runner exits with a nonzero status on failure; use the scripts below rather than `dotnet test`.

```powershell
# Deterministic tests; no engine network calls
.\scripts\test.ps1

# Integration test against bundled local Stockfish (no analysis network calls)
.\scripts\test.ps1 -LocalEngine

# Optional interactive desktop test; temporarily opens a sample board
.\scripts\test.ps1 -Gui
```

Tests cover board detection, recognition, orientation, move legality, caching, request deduplication, cancellation, and retry backoff. The GUI test additionally verifies live screen capture, both arrows, click-through behavior, and pausing. Reports and generated test images go in `artifacts/test-results/`.

The GUI test also opens and closes the detected-board preview, reverses its animation mid-transition, checks screen-edge placement, and verifies preview updates without extra engine requests. The preview renders the recognition result in a consistent piece style, including uncertain observations that are never sent for analysis. Pausing or losing the board clears it. Its 150ms expansion animation follows the Windows animation preference and responds immediately with an ease-out curve. Reversals shorten their duration to match the remaining distance. A cached board bitmap is prepared before the transition and reused until the position, moves, orientation, size, or DPI changes. Monitor geometry is calculated once per transition, and each frame batches movement and resizing into one window update.

The preview uses embedded Chess.com Neo PNGs from `assets/pieces/neo/` and reuses the screen overlay's arrow renderer and analysis results. Position changes, uncertainty, pause, and depth changes clear its arrows; late results for another position are ignored. The GUI suite checks these cases and flipped orientation.

GitHub Actions runs the deterministic tests and publishes a downloadable Windows build on pushes and pull requests. Interactive GUI tests and real-engine checks are opt-in local checks. The first build downloads the pinned Stockfish distribution and verifies its SHA-256; subsequent builds reuse it. The finished executable is fully offline.

### Analyze an image

To run board detection, recognition, and real engine analysis on another image:

```powershell
dotnet run --project tests/FishEyes.Tests -c Release -- --analyze-image --sample "path/to/board.png" --output artifacts/image-example/analysis.json
```

This uses depth 12 and saves a recognition/analysis report plus `move-arrows.png` beside the report. It uses the application's own arrow renderer and checks recognition confidence and move legality before producing the image.

To diagnose a detection or recognition failure without calling the engine:

```powershell
dotnet run --project tests/FishEyes.Tests -c Release -- --inspect-image --sample "path/to/board.png" --output artifacts/inspection/report.json
```

The report includes grid bounds, matched theme, each square's match error, runner-up margin and annotation coverage, and the final recognized position.

To make a README image with a board on the left and the actual FishEyes controls on the right:

```powershell
dotnet run --project tests/FishEyes.Tests -c Release -- --readme-image --sample "path/to/board.png" --output artifacts/readme-capture/report.json
```

This recognizes the supplied screenshot, gets both depth-12 engine results (using the normal cache), and renders the application's controls and arrows directly into `board-and-overlay.png`. It is a composed documentation image, not a desktop screenshot. This avoids Windows capture exclusion removing the overlay. The report records the position and engine moves used; no desktop windows are shown or captured by this command.

## Project structure

```text
FishEyes.slnx
src/FishEyes/
  Core/             Chess positions and move validation
  Services/         Screen capture and cached engine requests
  Vision/           Grid detection, theme matching and ONNX recognition
  UI/               Control panel and arrow overlay
  Program.cs        Application entry point
tests/FishEyes.Tests/
  Fixtures/         Sample board used by tests
assets/models/      Bundled ONNX model
assets/themes/      Bundled Chess.com templates and source manifest
assets/engine/      Stockfish download manifest (archive fetched during build)
scripts/            Build and test entry points
docs/               User guide and screenshots
licenses/           Third-party notices
.github/workflows/  Windows CI build
```

Generated build output, IDE files, and test reports are ignored by Git. Publishing also copies `FishEyes.exe` to the root for convenience. The offline executable exceeds GitHub's 100 MiB Git file limit, so it is ignored and distributed through GitHub Releases or Actions artifacts.

## Piece themes

Recognition first compares each square against a consistent Chess.com theme. A coarse pass ranks the bundled sets, then full comparisons refine the best three with small positional offsets. Transparent sprites are composited against the estimated square color, allowing textured and highlighted boards. Ambiguous or poor template matches are rejected. The last successful theme is tried first on subsequent frames; changing skins triggers another catalog search. The original ONNX classifier provides a fallback for other artwork.

Grid bounds are refined against the actual color transitions at native screen resolution, avoiding the extra pixel introduced by contour outlines. Template confidence values are fit scores, not calibrated probabilities.

Captured tiles and premultiplied sprite templates receive the same light Gaussian smoothing at 40px resolution. This reduces thin-outline differences caused by browser scaling without relaxing the absolute-error or ambiguity gates. Regression fixtures include a Bases board with highlighted squares and a review badge, checked at three sizes.

Before template matching, a board-wide annotation mask finds saturated, connected strokes spanning multiple squares. Per-square background estimates separate strokes from similarly colored board themes; component span and area checks keep ordinary piece artwork and highlighted squares out of the mask. Matching skips the masked pixels, including a small margin for antialiasing, and estimates square backgrounds from unmasked corners. It does not paint over the screenshot or infer hidden artwork from an earlier frame.

Squares with more than 45% of the matching region masked are rejected, alongside the existing absolute-error and ambiguity checks. If a detected annotation leaves the theme match uncertain, the unmasked ONNX classifier cannot override that rejection. Very faint strokes, short marks confined to one square, and heavily overlapping arrows may still require clearing annotations. Regression tests cover the supplied Band Class planning-arrow screenshot at three scales, four annotation colors on a similarly colored board, straight/diagonal/knight arrows, multiple arrows, both orientations, annotation removal, real moves beneath persistent annotations, and heavy-occlusion rejection.

The bundled pack covers 69 distinguishable 2D sets. See [theme coverage and refresh instructions](../assets/themes/README.md) for sources, exclusions, and the full synthetic test suite. Ordinary startup and recognition do not download images or contact Chess.com.

## Capture, local engine, and cache

Screen images are processed in memory and are not saved during normal use. No screenshots or positions leave the PC. `EngineService` sends FEN positions through redirected stdin to one hidden Stockfish 19 process using UCI, then reads `info` and `bestmove` from stdout. The process is reused, has below-normal priority, uses 1–2 threads, a 128 MiB hash table and MultiPV 1. Total engine memory is higher than the hash allocation.

The selected depth is a target from 1 to 40. Searches use `go depth <target> movetime 500`, stopping on either limit. Each side is searched independently and sequentially, so both can take roughly one second plus startup/communication overhead. The UI reports completed PV depth. Evaluations and mate signs are converted from the UCI side-to-move perspective to White's perspective. Returned moves still pass application legality checks.

The full official distribution (engine, source, license, authors and build docs) is embedded. First analysis verifies and extracts it under `%LOCALAPPDATA%\FishEyes\engines`; later process launches verify the executable. A file lock serializes installation across instances, and interrupted extraction is retried. Download URL and hashes are pinned in `assets/engine/stockfish.json`. Distribution notices and exact source access are included; see `assets/engine/README.md`.

The cache is stored at `%LOCALAPPDATA%\FishEyes\analysis-cache-local-v1.json`. It does not reuse old API results. Keys include engine version/protocol revision, threads, hash, time budget, target depth and the full FEN (including side to move). Completed results survive restarts; concurrent requests share work. Transient failures back off from 15 seconds to 5 minutes.

Board changes, uncertainty, depth changes and Off cancel pending analysis. Cancellation kills the process to discard stale output; the next request starts a clean process. A 15-second watchdog covers extraction, startup and protocol response. Exited engines restart automatically. Closing FishEyes kills its engine process. Tests use a real UCI child-process fixture for pipe handling, score signs, mate, timeout, crash, cancellation and process disposal; `-LocalEngine` verifies the official binary, process reuse and recovery.

Screens continue updating while the engine is working. Stale results cannot replace the current arrows. FishEyes excludes its own windows from capture, with a brief hide-and-capture fallback when Windows cannot exclude them.

Capture uses a single asynchronous loop: the next scan begins as soon as the previous one finishes, yielding to the UI without a fixed polling interval. Two matching recognized frames are still required before analysis, so piece animations do not immediately generate requests. Rapid Off/On toggles reuse the active loop and discard results from the previous generation. Engine requests remain serialized and deduplicated; error retry backoff is independent of screen scanning.

Both overlay windows reassert their topmost position on foreground-window changes, with a 750ms fallback check. This uses `SetWindowPos` without activation, movement, resizing, or showing hidden windows. The panel stays above its arrow layer, including while analysis is paused. The GUI test verifies recovery above another topmost window and preservation of foreground keyboard focus.

## Limitations

- Requires an axis-aligned 2D board, entirely visible on one monitor. If multiple boards are visible, the largest confidently recognized board is selected.
- Unfamiliar piece themes, low contrast, animations, or very small boards can prevent recognition. Low-confidence and invalid positions are skipped.
- Orientation is inferred from piece placement and maintained across nearby positions. Starting directly in an unusual endgame may infer it incorrectly.
- Castling and en passant rights cannot be established from screenshots and are assumed unavailable. Repetition and the fifty-move rule are not tracked.
- Capture speed depends on board recognition and screen size. Continuous scanning may use more CPU than fixed-interval polling.

## Dependencies and licensing

FishEyes uses .NET / Windows Forms, ONNX Runtime, OpenCvSharp / OpenCV, and the [chessvision recognition model](https://huggingface.co/harshitpawar64/chessvision) by Harshit Pawar. See [third-party notices](../licenses/README.txt) and [model information](../assets/models/README.md). Stockfish 19 is bundled as an unmodified separate UCI executable under GPLv3, with the exact upstream source distribution and license.

No license has been selected for the original FishEyes application code. The files under `licenses/` describe third-party components and do not license the application as a whole.
