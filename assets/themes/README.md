# Chess.com piece templates

`chesscom-templates.br` embeds 69 distinguishable 2D piece sets from Chess.com's public theme catalog, including Band Class and the available bot/event skins. It works offline; users do not select or download a theme.

The pack contains 40 × 40 premultiplied BGRA samples for each of 12 pieces, compressed with Brotli. `manifest.json` records each catalog entry, original public image URL, SHA-256 of the downloaded image, and any exclusion. The artwork belongs to Chess.com and its respective creators; no ownership or open-source license for that artwork is claimed here.

## Coverage

Snapshot: 2026-09-25. All 960 publicly listed images were downloaded locally. 69 sets passed 138 synthetic board cases using the original 150px sprites: starting position and middlegame, both orientations, 56px and 87px squares, two board palettes, coordinates, texture, and highlights. Each case finds the board automatically inside a larger image. The original user-supplied Band Class screenshot also passes automatic grid detection and exact position recognition.

Excluded catalog entries:

| Sets | Reason |
| --- | --- |
| Blindfold | Pieces are invisible. |
| Bots - War is over | White and Black use identical artwork, so their sides cannot be distinguished. |
| Checkers - Neo 1, 2, 3; Checkers 4 | Artwork represents checkers, not distinguishable chess pieces. |
| 3D - ChessKid, Wood, Staunton, Plastic; Real 3D | Perspective or overlapping pieces require a different recognition pipeline. |

This is a tested catalog snapshot, not a guarantee for every browser rendering or future skin. Small boards, animations, occlusion, unusual scaling, and low contrast can still prevent recognition. The ONNX model remains a fallback for other 2D artwork.

## Refresh and verify

From the repository root, using PowerShell 7 and .NET 10:

```powershell
.\scripts\update-piece-themes.ps1
```

The script reads the public catalog, downloads sprites into ignored `artifacts/themes/`, regenerates the pack and manifest, then tests every included theme. It requires no Chess.com login. Downloads are bounded to six concurrent requests, and completed files are reused. Use `-UseCachedCatalog` to rebuild the same snapshot. Failed tests must be reviewed before publishing an updated pack.

To rerun the full theme suite without downloading:

```powershell
dotnet run --project tests/FishEyes.Tests -c Release -- --theme-tests --sample artifacts/themes --output artifacts/theme-tests/results.json
```

Normal builds use the checked-in pack and need no Chess.com connection. The standard offline test suite includes the actual Band Class screenshot as a regression fixture.
