# LocalMind identity assets

## Mark

The LocalMind mark is an original generated asset: two offset folded arches suggest an open doorway and a lowercase m. A slate-blue field contains an ivory ribbon and one muted-blue fold. The product uses this motif as its main visual signature while keeping working surfaces restrained.

- Original generated PNG: `apps/desktop/public/brand/localmind-mark.png` (1254 × 1254).
- UI asset: `apps/desktop/public/brand/localmind-mark-256.png` (256 × 256).
- Browser favicon: `apps/desktop/public/brand/favicon.png` (32 × 32).
- Desktop bundle assets: `apps/desktop/src-tauri/icons/`, including ICO, ICNS, PNG, and Windows Store sizes.

The original was copied unchanged from the image-generation result. Derived sizes and platform formats were produced with the installed Tauri icon command, not redrawn or manually retouched. The existing Tauri bundle icon paths now resolve to this identity. Native installers must be rebuilt to contain the updated icon.

Original generation prompt, retained verbatim for provenance:

> Use case: logo-brand. Create one finished original app icon for LocalMind, a private desktop thinking and knowledge workspace. Exact asset: a square 1024x1024 flat icon, centered bold abstract mark with generous 18% margin, no text, no wordmark, no mockup, no shadows, no lighting, no gradients. Design the mark as a distinctive continuous folded pathway: two offset arch-like ribbon forms interlock to suggest an abstract lowercase m plus an open doorway. Asymmetric but balanced, precise uniform thick geometric strokes, softly squared corners, a small square negative-space opening. It must remain legible at 24px. Background solid deep slate blue #24475F; mark solid pale ice #EEF4F8 with one restrained mid-blue #89B0CA plane for a single folded facet. It should feel like a meticulously drawn software brand, not a brain, not a robot, not a chat bubble, not a sparkle, not an infinity symbol, no familiar corporate logo. Fill the entire square background edge to edge; no rounded outer border, just the central geometric symbol. Deliver only the single icon.

The delivered image is 1254 × 1254 and contains subtle raster shading. These are properties of the actual result, despite the flat 1024px request. The 128px conversion was visually inspected. This is an original design direction, not a trademark clearance assertion.

To regenerate platform formats from the saved original, run from the repository root:

```powershell
node apps/desktop/node_modules/@tauri-apps/cli/tauri.js icon apps/desktop/public/brand/localmind-mark.png --output apps/desktop/src-tauri/icons
```

The converter also emits mobile icon directories; they are unnecessary for the current desktop targets and are not included here. The favicon is a copy of `32x32.png`; the UI mark is a copy of `128x128@2x.png`.

## Typography

IBM Plex Sans is bundled in the application. It was selected for its readable humanist forms and compact interface metrics. No external font request is needed at runtime, and no system-wide font installation is required.

Files are unmodified Latin-subset WOFF2 assets from the pinned `@fontsource/ibm-plex-sans@5.3.0` distribution:

| Weight | Local file | Bytes |
| --- | --- | --- |
| Regular 400 | `apps/desktop/public/fonts/ibm-plex-sans-latin-400-normal.woff2` | 22,588 |
| Medium 500 | `apps/desktop/public/fonts/ibm-plex-sans-latin-500-normal.woff2` | 24,184 |
| Semibold 600 | `apps/desktop/public/fonts/ibm-plex-sans-latin-600-normal.woff2` | 24,252 |

The original typeface is maintained in [IBM/plex](https://github.com/IBM/plex). Assets came from the [versioned Fontsource distribution](https://cdn.jsdelivr.net/npm/@fontsource/ibm-plex-sans@5.3.0/), with its accompanying SIL Open Font License saved as `apps/desktop/public/fonts/OFL-IBM-Plex-Sans.txt`. Preserve that license when redistributing the app. All three files were checked for a valid WOFF2 signature.

Only Latin is bundled in these files. The CSS fallback stack should retain appropriate system fonts for other scripts; no claim is made that this subset covers the entire IBM Plex multilingual family. Use `font-display: swap` in the font declarations. The HTML preloads the regular body weight from the local application origin.

## Asset usage

Use the 256px mark for normal UI images with explicit width and height. Keep the master PNG as the source asset, rather than loading it repeatedly in the transcript. Do not stretch the mark, replace its colors ad hoc, or add decorative glow. The visible wordmark remains ordinary selectable text beside the icon. Functional controls use the existing SVG icon library, not the logo as a substitute for an action symbol.
