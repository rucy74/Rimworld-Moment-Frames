# MomentFrames native fonts

Four full-cmap OFL fonts accompany the album's optional handwriting. They are loaded from `momentframesfonts` as Unity Font assets; no OS font installation or registry change is required. The three TTFs other than `MF-light.ttf` are kept in the source tree to rebuild the bundle and are left out of the installed mod (`tools/deploy.ps1`); the HTML export copies `MF-light.ttf`.

| Setting | Original | Native family | Hangul syllables |
|---|---|---|---:|
| Light pencil | Gaegu Light | Moment Frames Light | 2350 |
| Pencil handwriting | Gaegu Regular | Moment Frames Pencil | 2350 |
| Pen handwriting | Nanum Pen Script | Moment Frames Pen | 11172 |
| Brush handwriting | Nanum Brush Script | Moment Frames Brush | 11172 |

Only family/style names were modified. Original glyphs, cmaps and advances are retained; the original copyright/OFL notices are in the four `*-OFL.txt` files. Unique families respect the Nanum reserved names. `manifest.json` records source/output hashes and original advances. `.ttf.coverage` records each font's exact BMP cmap. Unsupported letters or symbols use the game's font for that label; drawing and height measurement follow the same fallback path.

`momentframesfonts` contains four Font assets with precisely these TTF bytes, four standard font materials and four empty dynamic atlases, plus the bundle object. It uses Unity's built-in font shader. `bundle-manifest.json` records hashes and the local empty serialization template. Its schema/default metadata originated from ZCube/rw-customfonts and nmkj-io/rw-customfonts; their MIT notice is included in this runtime folder as `Schema-MIT.txt`. The stripped template and provenance are documented in `tools/font-bundle-template/README.md`. Font OFL licenses remain independent of the template license.

Rebuild from the repository root with Python, fontTools and UnityPy:

```powershell
python tools/build-native-fonts.py
python tools/build-font-bundle.py
```

The first builder uses preserved original sources under `runs/ui-themes-{sketch,watercolor}-2026-10-03/font-originals`; those ignored source directories must be retained when moving workspaces. The runtime package already contains complete renamed TTFs and the bundle. The second builder uses the checked-in empty template and has no installed Workshop-mod dependency.

Native evidence and platform limits: `docs/native-appearance.md`. The browser's corpus subsets in `docs/ui-themes` are separate artifacts and have not replaced these full font payloads.
