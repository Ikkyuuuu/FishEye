# FishEye

<img src="https://github.com/user-attachments/assets/26c3bc62-fb13-40a0-86dd-34a20ebb0292" width="100%" alt="The Queen's Gambit"><br><br>

Have you ever looked at a chessboard and wondered what the best next move is?

FishEye watches your screen, detects the pieces, and asks Stockfish to figure it out. Then it draws an arrow from the piece to where it should move. Just keep the board visible and turn it on.<br><br>

<img src="docs/images/overlay.png" width="100%" alt="FishEyes overlay"><br><br>

## Key Feature

- Automatically finds the board and pieces, without cropping.
- Checks your screen about once every second.
- Blue arrow for White, orange arrow for Black.
- Adjustable search depth from 1 to 15.
- Remembers analyzed positions, so it doesn't keep asking the API the same thing at the same depth.

Both arrows use the current board, each assuming that side is to move.<br><br>

## Move Suggestion

Here's what it looks like from the starting position.<br><br>

<img src="docs/images/move-arrows.png" width="100%" alt="Starting position with move suggestions for White and Black"><br><br>

And on a different board. At depth 12, White gets **b7 → f7** and Black gets **a8 → c8**.<br><br>

<img src="docs/images/move-arrows-2.png" width="100%" alt="White rook moves from b7 to f7; Black rook moves from a8 to c8"><br><br>

## How to Use

1. Open `FishEyes.exe`.
2. Keep the full chessboard visible on your screen.
3. Choose the search depth and switch **On**.
4. Wait for the arrows. Higher depth may take longer.

Switch **Off** to pause, drag the FishEye header to move it, or press **×** to close.<br><br>

## Build from Source

You'll need Windows x64, the .NET 10 SDK, and PowerShell.

```powershell
.\scripts\build.ps1 -Test
```

Then open `artifacts/publish/win-x64/FishEyes.exe`.

If you just wanna run the source:

```powershell
dotnet run --project src/FishEyes
```

## Note

Keep the board clear and fully visible. Some piece themes and unusual endgames may be misread. Castling and en passant aren't included in the suggestions.

Your screenshots stay on your PC. Only the board position and depth go to [StockfishOnline](https://stockfish.online/docs.php), so new analysis needs internet.

For more detail, check out the [development notes](docs/DEVELOPMENT.md). Piece recognition uses [chessvision](https://huggingface.co/harshitpawar64/chessvision); credits are in [third-party notices](licenses/README.txt).
