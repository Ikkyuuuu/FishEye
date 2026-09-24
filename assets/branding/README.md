# FishEyes mark

The mark keeps the original eye outline and pupil, with a triangular tail on the right so it also reads as a fish.

- `logo.svg`: editable vector source.
- `logo.png`: transparent header asset, generated from the SVG.
- `FishEyes.ico`: Windows icon with 16, 20, 24, 32, 40, 48, 64, 128, and 256 pixel variants.
- `app-icon.png`: preview of the icon on its rounded dark background.

Regenerate the PNG and ICO assets on Windows after changing the vector:

```powershell
.\scripts\generate-icon.ps1
```

The small generator supports the ellipse, circle, and polygon elements used by this mark. Generated assets are committed so normal builds do not require an extra asset generation step. The application embeds the header asset and sets both its window icon and executable icon.
