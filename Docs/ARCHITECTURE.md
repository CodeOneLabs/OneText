# OneText architecture

The pipeline follows the industry-standard text stack (the same shape used by
browsers and Android):

```
string
  │
  ▼
1. Parse          markup → plain text + styled ranges
  │
  ▼
2. Analyze        Unicode: script itemization, BiDi runs (UAX #9),
  │               line-break opportunities (UAX #14), segmentation (UAX #29)
  ▼
3. Shape          HarfBuzz per run: GSUB/GPOS → glyph IDs + advances/offsets
  │
  ▼
4. Layout         line breaking, alignment, justification; style effects
  │
  ▼
5. Render         FreeType outline extraction → Burst SDF rasterization →
                  Texture2DArray atlas → mesh generation → frontend
```

## Module map

| Module | asmdef | Depends on | Contents |
|---|---|---|---|
| Core | `OneText` | Burst, Collections, Mathematics | pipeline stages 1 through 5, up to mesh data; native bindings; font management; atlas |
| UGUI | `OneText.UGUI` | Core, UnityEngine.UI | `OneTextUGUI : MaskableGraphic, ILayoutElement`; input field; menu items |
| Editor | `OneText.Editor` | Core, UGUI | font asset importer, inspectors, tools window |

**Rule: Core never references a UI framework.** Frontends consume
`TextRenderData` (positioned quads + atlas UVs + colors) and turn it into
whatever their framework needs. This is what keeps a future UI Toolkit or
world-space frontend cheap.

## uGUI integration (design goal, M5)

The TMP experience we want to keep, and the pain we want to remove:

Keep:
- `MaskableGraphic` + `ILayoutElement`: layout groups, `ContentSizeFitter`,
  masks, raycast targets all just work.
- GameObject-menu creation, sensible defaults, a default font that is
  auto-assigned so a fresh component renders immediately.
- Familiar API surface: `text`, `fontSize`, `color`, `alignment`,
  `raycastTarget`, `preferredWidth/Height`.

Remove (TMP pain points):
- No per-font material/submesh juggling exposed to the user; the shared
  atlas + single shader handles the common path.
- No manual atlas baking. Glyphs rasterize on demand at runtime and in-editor.
- Fallback configured once at the project level (font stack asset), not
  per-asset chains that silently miss.

## Native strategy (M1)

- **HarfBuzzSharp / FreeType prebuilt natives** (MIT-licensed, maintained by
  the SkiaSharp project) to skip the 6-platform cross-compile wall initially.
  P/Invoke bindings live in `Runtime/Core/Native`.
- Fonts load from memory (`FT_New_Memory_Face`, `hb_face_create`): font bytes
  are embedded in a `ScriptableObject` asset; no file I/O at runtime.
- Shaping and rasterization run off the main thread; native handles are
  confined per-thread or locked.

## Testing strategy

- Unicode algorithms: run the official UCD test files as unit tests.
- Shaping: compare against HarfBuzz's own `test/shape` expectations.
- Rendering: golden-image tests per script sample (Arabic, Devanagari, Thai,
  Korean, emoji) on CI.
