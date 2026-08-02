# Photo Importer icon source notes

The application icon is a raster asset generated with the built-in OpenAI ImageGen tool and selected as color variant A on 2026-08-02.

## Files

- `PhotoImporter.Icon.A.Chroma.png`: original ImageGen output on a removable magenta background.
- `PhotoImporter.Icon.A.Master.png`: transparent high-resolution master after chroma-key removal.
- `../../src/PhotoImporter.App/Assets/PhotoImporter.Icon.png`: application master copied from the selected transparent master.
- `../../src/PhotoImporter.App/Assets/PhotoImporter.ico`: Windows icon generated from the application master.

The ImageGen result is not a layered or vector source. Preserve the chroma image, transparent master, palette, and prompt together when changing the icon in the future.

## Visual specification

- Product: Photo Importer
- Composition: white photo frame with mountains and sun, combined with a downward import arrow
- Colored body: royal-blue gradient, approximately `#075FD8` to `#2494F2`
- Glyphs: white, approximately `#FFFFFF`
- Outside background: transparent
- Text, watermark, cast shadow, and texture: none
- Required behavior: recognizable at 16 px; exact geometry and white glyphs should be preserved when recoloring

## ImageGen edit prompt

```text
Use case: precise-object-edit
Asset type: Windows application icon color variant A
Input image: edit target; preserve its geometry exactly
Primary request: Recolor only the existing blue/teal colored icon body to a clean royal-blue gradient, from #075FD8 to #2494F2. Keep the white photo frame, mountains, sun, and downward arrow pure white and unchanged.
Scene/backdrop: perfectly flat solid #ff00ff chroma-key background for later removal
Constraints: preserve exact silhouette, proportions, padding, line thickness, and all white shapes; no purple, magenta, teal, or green in the subject; no text, shadow, texture, added objects, or geometry changes. Background must be uniform #ff00ff with no variation.
```

## Processing settings

The transparent master was produced with the ImageGen skill's `remove_chroma_key.py` helper using:

```text
--auto-key border
--soft-matte
--transparent-threshold 12
--opaque-threshold 220
--despill
```

The `.ico` contains these sizes:

```text
16, 20, 24, 32, 40, 48, 64, 128, 256 px
```

Generate the ICO from the transparent RGBA master with Pillow and LANCZOS-compatible ICO resizing. After any change, inspect at 16, 24, 32, 48, and 256 px, then run a Release build to verify both the executable icon and WPF window icons.
