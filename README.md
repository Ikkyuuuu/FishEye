# FishEye

<img src="https://github.com/user-attachments/assets/26c3bc62-fb13-40a0-86dd-34a20ebb0292" width="100%" alt="The Queen's Gambit"><br><br>

Have you ever looked at a chessboard and wondered what the best next move is?

FishEye watches your screen, detects the pieces, and uses the bundled Stockfish engine to figure out the next move. Everything runs on your PC, even without internet. Just keep the board visible and turn it on.<br><br>

<img src="docs/images/overlay.png" width="100%" alt="Chessboard with move arrows and the FishEyes panel on the right, including its detected-board preview"><br><br>

## Key Feature

- Automatically finds the board and pieces, without cropping.
- Recognizes 69 Chess.com 2D piece skins, including Band Class. No skin selection needed.
- Checks again as soon as each scan finishes, with no fixed polling delay.
- Blue arrow for White, orange arrow for Black—on your screen and in the preview.
- Stockfish 19 included. Works offline, with no account or separate engine install.
- Target depth from 1 to 40, with up to 500 ms of thinking per side. Shows the depth actually reached.
- Board preview with Chess.com's Neo pieces and a quick 150 ms dropdown animation.
- Stays above other windows without taking keyboard focus, even while paused.
- Remembers analyzed positions, so it doesn't calculate the same thing again at the same settings.

Both arrows use the current board, each assuming that side is to move.<br><br>

## Board Preview

Wanna check if the pieces were read correctly? Click **Detected board** at the bottom of the overlay. It opens with a quick animation and shows the pieces FishEye sees, using Chess.com's Neo skin.

The preview follows the board's orientation and shows the same move arrows as your screen. It updates as you play, and old arrows disappear when the position changes. Click **Detected board** again to tuck it away.

Uncertain detections are labeled so you can inspect them, but aren't sent for analysis. Switching **Off** clears the preview too.<br><br>

## Move Suggestion

Here's what it looks like from the starting position.<br><br>

<img src="docs/images/move-arrows.png" width="100%" alt="Starting position with move suggestions for White and Black"><br><br>

And on a different board. This earlier API example shows White playing **b7 → f7** and Black playing **a8 → c8**. Local results can differ.<br><br>

<img src="docs/images/move-arrows-2.png" width="100%" alt="White rook moves from b7 to f7; Black rook moves from a8 to c8"><br><br>

## Chess.com Piece Skins

Using a different skin? FishEye automatically recognizes 69 Chess.com 2D piece sets, including the available bot and event skins. The templates are bundled, so there's nothing extra to download or select.

Recognition is also improved for the Bases skin at different board sizes, including highlighted squares and move-review badges.

Drawing planning arrows? FishEye can ignore colored strokes that cross squares while reading the visible pieces. If arrows cover too much, the preview stays marked uncertain until you clear them.

Here's Band Class, with an earlier API example showing **c3 → d5** for White and **c7 → c6** for Black.<br><br>

<img src="docs/images/move-arrows-band-class.png" width="100%" alt="Band Class skin with a blue knight arrow from c3 to d5 and an orange pawn arrow from c7 to c6"><br><br>

## How to Use

1. Download `FishEyes.exe` from [Releases](https://github.com/Ikkyuuuu/FishEye/releases) and open it.
2. Keep the full chessboard visible on your screen.
3. Choose the search depth and switch **On**.
4. Wait for the arrows. The first analysis also unpacks Stockfish; later searches reuse it.
5. Open **Detected board** whenever you wanna check the detection.

Switch **Off** to pause, drag the FishEye header to move it, or press **×** to close.

Wanna take a screenshot? Close FishEye and open `FishEyes-Window.cmd` beside the EXE. It opens a normal window you can capture, with the arrows in its board preview.<br><br>

## Offline Stockfish

Stockfish is inside the EXE, so there's nothing else to install. The download is about **186 MB**. The first analysis takes a little longer while FishEye unpacks the engine; after that, it reuses the same process.

Each side gets up to **500 ms** to think. The depth you choose is a target, and the overlay shows how far Stockfish actually got. Faster PCs can search deeper in that time. FishEye uses up to two CPU threads and a 128 MB hash table, plus the engine's other memory.

Your screenshots and board positions stay on your PC. Results are saved between runs, and old analysis is cancelled when the board changes or you switch Off.<br><br>

## Build from Source

You'll need Windows x64, the .NET 10 SDK, and PowerShell.

```powershell
.\scripts\build.ps1 -Test
```

Then open `artifacts/publish/win-x64/FishEyes.exe`.

The first build needs internet to download dependencies and the verified Stockfish package. The finished app works offline. You'll also find a copy of `FishEyes.exe` in the root folder after building.

The EXE is too large to keep in Git. Download published versions from [Releases](https://github.com/Ikkyuuuu/FishEye/releases), or get the latest build artifact from [Actions](https://github.com/Ikkyuuuu/FishEye/actions).

If you just wanna run the source:

```powershell
dotnet run --project src/FishEyes
```

## Note

Keep the board clear and fully visible. 3D, blindfold, and checkers skins aren't supported. New skins and unusual endgames may still be misread. See [skin coverage](assets/themes/README.md). Castling and en passant aren't included in the suggestions.

Stockfish is unpacked under `%LOCALAPPDATA%\FishEyes\engines` with its GPLv3 license and source.

For more detail, check out the [development notes](docs/DEVELOPMENT.md). Piece recognition uses [chessvision](https://huggingface.co/harshitpawar64/chessvision); credits are in [third-party notices](licenses/README.txt).
