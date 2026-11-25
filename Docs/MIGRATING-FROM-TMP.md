# Migrating from TextMesh Pro

This guide is for a project that draws its text with TextMesh Pro, or with
Unity's older `UnityEngine.UI.Text` and `TextMesh`, and wants to draw it with
OneText. It says what the tooling does, how to read what it reports, what
each component, member and tag turns into, and what has no counterpart. It
describes OneText 0.5.0.

## Two routes

**The Onboarding tab.** *Project Settings > OneText > Onboarding*; the menu
*Window > OneText > Hub* opens the same settings page. Step 1 converts the
text components in scenes and prefabs. Step 2 rewrites the TMP type names in
the scripts under `Assets`. Both scan first and write nothing until a button
is pressed. The first time the package loads into a project it asks, once,
whether to open this tab.

**By hand.** Add the OneText components yourself (*GameObject > UI > OneText >
Label*, *Input Field*, *Dropdown*; *GameObject > 3D Object > OneText > Text
Mesh*), make a font asset from each font file (*Assets > OneText > Create Font
Asset*), and change the code with the [API map](#api-map) below. The lowercase
TMP member names still compile on the OneText components, so most call sites
need only their type changed.

The two mix. A partial run is supported: tick some scenes and prefabs under
**Where they are**, and the button becomes *Convert N component(s) in M
container(s)*. Three things happen that a tick does not show:

- The run widens to every prefab the ticked files are built out of, and says
  so (`nested-selection`). A label inside a nested prefab has to be converted
  in that prefab, not as an override on top of it.
- Prefabs, scenes and ScriptableObjects elsewhere in the project that hold a
  field naming a converted component are opened afterwards and re-pointed,
  as long as *Fix references that cross from one scene or prefab into
  another* is on, which is the default. They are saved only if a field in
  them moved, so they appear in the diff.
- Anything already converted is skipped. Scan again to see what is left.

## Before you start

- **Commit.** Converting saves scenes and prefabs, and rewriting overwrites
  scripts. Both buttons ask git whether the files they are about to touch are
  committed and say so in the confirmation, but Unity cannot undo either.
- **Leave TextMesh Pro installed until Step 1 is done.** The code that reads
  TMP components compiles only where TMP does (`com.unity.textmeshpro` 3.0.0
  or newer, or `com.unity.ugui` 2.0.0 or newer). Without it the tab says so
  and looks for the legacy components only.
- **Keep the source font files.** OneText draws from the `.ttf`, `.otf` or
  `.ttc`. A TMP font asset whose source file is no longer in the project
  cannot be converted; see [Fonts](#fonts).
- **Fix missing scripts first.** A scene or prefab that holds a script Unity
  cannot resolve is reported and left untouched (`unsaveable-container`).

## Running the tab

1. **Scan Scenes & Prefabs.** Opens every prefab under `Assets` and every
   scene in Build Settings, reads every component including inactive ones, and
   saves nothing. *Also scan scenes that are not in Build Settings* widens it
   to every scene under `Assets`. If open scenes have unsaved changes Unity
   asks about them first; cancelling that prompt stops the run (`cancelled`).
2. **Read the report** (next section).
3. **Convert.** The scan is run again from scratch and only then are
   components replaced. Prefabs go before scenes, and a base prefab before
   anything built out of it. Labels and world text go first, then input
   fields, then dropdowns. Font assets are made once per source file.
   References to a replaced component are re-pointed and read back. The
   progress bar can be cancelled between files: what was already converted is
   saved, references into it are still mended, and running again carries on
   from the state of the project.
4. **Scan Assets**, then **Rewrite**. Reads every `.cs` file under `Assets`
   (packages are left alone), shows a diff per file, and writes the ticked
   files. See [What the script rewrite does](#what-the-script-rewrite-does).

The two steps depend on each other in one way. A field declared
`TextMeshProUGUI` or `TMP_Text` cannot hold a `OneTextLabel`, so a component
named by such a field is held back in Step 1 with `reference-would-break`.
Run Step 2, let Unity recompile, and convert again: the field is now declared
as the OneText type and the held-back components convert. Between the rewrite
and that second conversion, those fields are empty in the loaded scene; the
reference is still in the file, and the conversion reads it from there.
Rewriting the scripts first and converting once also works.

## Reading the scan

Findings have three severities. **Errors** do not come across and need you.
**Warnings** come across differently and should be looked at. **Notes** are
arithmetic worth knowing; they are hidden until *Show notes* is switched on.
Findings are grouped by rule id, and each names the component, its hierarchy
path and its file.

Rules a scan can raise:

| Rule | Severity | Meaning, and what to do |
|---|---|---|
| `unsupported-tag` | warning | The text uses TMP tags OneText does not parse. They print literally. Nothing is rewritten: edit the string ([Rich text](#rich-text)). |
| `margin-lost` | warning | The label has a TMP margin. OneText has none; inset the RectTransform by the same amounts. |
| `overflow-approximated` | warning | The overflow mode is Masking, ScrollRect, Page or Linked. The nearest mode is set ([table](#members-that-convert)). |
| `font-style` | warning | The component's font style is not Normal. Styles are not held on the component: wrap the text in `<b>` or `<i>`, or assign a `OneTextStyle`. |
| `material-effect` | warning, note | Something on the TMP material is not carried, or is carried with a caveat ([Materials](#materials-and-effects)). |
| `no-counterpart` | warning | A feature with nothing to map to: an input field's content type or character validation, an input field with no text component, a dropdown's multi-select or placeholder, sprite or animation tags on world text. |
| `cross-container-reference` | warning | A field in this file names a component in another file. Converting that file breaks the field; this file is opened afterwards and the field re-pointed. Listed so the write is not a surprise in the diff. |
| `unreadable-container` | warning | A scene or prefab could not be opened. It is skipped. |
| `font-source-missing` | error | The font has no usable file in the project. Also raised during a conversion when no font asset could be made from the file. See [Fonts](#fonts). |
| `reference-would-break` | error | The component is left as it is, because a field naming it is declared as a type that cannot hold the replacement. For a TMP type: run the script rewrite, recompile, convert again. For `Text` or another type the rewrite does not touch: change the declaration yourself, or leave the component. |
| `severed-reference` | error | The file names a field that the component no longer has. The reference cannot be restored; it is reported so the loss is known. |
| `cancelled` | error | Open scenes had unsaved changes and were not saved. Nothing was scanned. |
| `alignment-approximated` | note | The alignment used Flush, GeoAligned, Baseline, Midline or Capline. The nearest alignment is set. |
| `line-spacing-approximated` | note | TMP's line spacing is an offset and OneText's is a multiplier. The intent is converted; the pixels may differ. |
| `font-default` | note | No font was assigned. The label uses the project default. |
| `size-approximated` | note | A legacy `TextMesh` sizes by `fontSize × characterSize`. The converted size is arithmetic; check it on screen. |
| `rect-added` | note | A legacy `TextMesh` has no RectTransform. One replaces its Transform; the position is kept. |

Rules only a conversion can raise:

| Rule | Severity | Meaning, and what to do |
|---|---|---|
| `nested-selection` | note | Prefabs were added to a partial run because the selection is built out of them. |
| `unsaveable-container` | error | The file holds a missing script. Unity will not save it correctly, so nothing in it was converted. Remove or restore the script and convert again. |
| `save-failed` | error | Components were swapped and Unity then refused to write the file. It is unchanged on disk; the console has Unity's reason. |
| `convert-failed` | error | One component could not be replaced. The message carries the exception. |
| `dangling-reference` | error | A field in the same file would not take the replacement and reads None. Its declared type is not what it appeared to be. Follow the remedy in the message, or set the field by hand. |
| `cross-container-reference` | note, warning, error | Note: a field in another file was re-pointed. Error: it could not be, and reads None; the message names the field and why. Warning: a file could not be opened to read its references, or a partial run left some fields unmended. |
| `event-listeners` | note, warning | Note: an input field's persistent listeners were carried. Warning: some were not, because the method is not on the new type or the target is gone; re-wire them. |
| `no-counterpart` | warning | An input field's or dropdown's label was not converted in this run, so the new component has none. Assign a `OneTextLabel` by hand. |
| `font-recovered` | note | A placeholder font asset was made for a font with no file. |
| `project-default` | note | The project default font or the global fallbacks were taken from TMP's settings. |
| `cancelled` | warning | The run was stopped part way. What was converted is saved. |

The script scan has no rule ids. It marks a file **refused** (rewriting it
would not compile), counts TMP names **left** over, and flags files in
vendor folders and assemblies that need a reference.

### On the command line

The component scan runs in batch mode. It never converts.

```
Unity -batchmode -quit -projectPath . -executeMethod \
      OneText.Editor.ComponentMigration.RunFromCommandLine -oneAllScenes
```

| Argument | Effect |
|---|---|
| `-oneAllScenes` | Scan every scene under `Assets`, not only those in Build Settings. |
| `-oneNoPrefabs` | Do not scan prefabs. |

Each finding is logged as `OneText migration <severity>: [rule] message`,
followed by a summary line with the component and container counts.

| Exit code | Meaning |
|---|---|
| 0 | The scan raised no error. Warnings and notes do not fail it. |
| 1 | At least one error-severity finding. |
| 2 | The scan itself threw. |

The exit code follows errors, not the component count: a project that still
holds TMP components which would all convert cleanly exits 0. To gate a merge
on "none left", read the summary line.

## Component map

| From | To | Namespace of the new type |
|---|---|---|
| `TextMeshProUGUI` | `OneTextLabel` | `OneText.UGUI` |
| `TextMeshPro` | `OneTextMesh` | `OneText` |
| `TMP_InputField` | `OneTextInputField` | `OneText.UGUI` |
| `TMP_Dropdown` | `OneTextDropdown` | `OneText.UGUI` |
| `UnityEngine.UI.Text` | `OneTextLabel` | `OneText.UGUI` |
| `TextMesh` | `OneTextMesh` | `OneText` |
| `UnityEngine.UI.InputField` | `OneTextInputField` | `OneText.UGUI` |
| `UnityEngine.UI.Dropdown` | `OneTextDropdown` | `OneText.UGUI` |

Subclasses of these are matched too. `OneTextLabel` is a `MaskableGraphic`,
so the RectTransform, layout groups, `ContentSizeFitter`, masks and
raycasting around it keep working.

**Carried from a TMP text component:** the text, the rich text switch, font
size, auto-size and its minimum and maximum, colour, raycast target,
alignment, overflow, wrapping, line spacing, the font and its fallback table,
and on a label the outline, shadow, glow and face weight read from its
material.

**Reported and not carried:** the margin, the font style, and material
properties with no counterpart.

**Not read, and so not reported either:** character, word and paragraph
spacing; the vertex colour gradient; the sprite asset; the style sheet.

**From `UnityEngine.UI.Text`:** text, rich text, font, font size, best fit
and its bounds, line spacing (a multiplier on both sides, copied as is),
colour, raycast target, the anchor as two alignments, horizontal overflow as
wrap and vertical overflow as overflow. **From `TextMesh`:** text, rich text,
font, colour, line spacing, alignment and anchor, and a size worked out from
`fontSize` and `characterSize`; it never wraps, and it gains a RectTransform.

## API map

### Members that still compile

These members exist on `OneTextLabel` under TMP's names. Most forward to the
PascalCase property that is the real API and are hidden from completion, so
new code reads OneText's own names.

| TMP member | Forwards to | Note |
|---|---|---|
| `text` | `Text` | |
| `fontSize` | `FontSize` | |
| `richText` | `RichText` | |
| `enableAutoSizing`, `fontSizeMin`, `fontSizeMax` | `AutoSize`, `AutoSizeMin`, `AutoSizeMax` | |
| `maxVisibleCharacters` | `MaxVisibleGraphemes` | Counts grapheme clusters, not UTF-16 units: Hangul or an emoji reveals in fewer steps. |
| `alpha` | the alpha of `color` | `color` itself is inherited from `Graphic`. |
| `SetText(string)`, `SetText(string, bool)`, `SetText(StringBuilder)` | `Text` | The builder overload allocates a string. |
| `ForceMeshUpdate()`, `ForceMeshUpdate(bool, bool)` | layout and geometry, now | Synchronous. `ignoreActiveState` is accepted and ignored. |
| `GetParsedText()` | `DisplayText` | |
| `isTextOverflowing`, `renderedWidth`, `renderedHeight` | the laid-out result | |
| `preferredWidth`, `preferredHeight` | the layout element values | |
| `textInfo`, `UpdateVertexData()`, `UpdateGeometry(Mesh, int)` | a TMP-shaped view of the layout | For per-character code that cannot be rewritten. New code should use `ITextQuadModifier`. |
| `faceColor` | the label's colour | There is no separate face colour. |
| `outlineColor`, `outlineWidth`, `glowColor` | `Decoration` | A transparent colour turns the part off. |
| `fontSharedMaterial`, `fontMaterial` | `Graphic.material` | Neither instantiates a material. |

`OneTextMesh` has a shorter list: `text`, `fontSize`, `color`, `alpha`,
`richText`, `enableAutoSizing`, `fontSizeMin`, `fontSizeMax`,
`ForceMeshUpdate`, `GetParsedText` and the three `SetText` overloads. It does
not have `alignment`, `lineSpacing`, `textWrappingMode`, `enableWordWrapping`
or `overflowMode`; world-text code that sets those has to use `Alignment`,
`VerticalAlignment`, `LineSpacing`, `Wrap` and `Overflow` in OneText's units.

`OneTextInputField` and `OneTextDropdown` use the input-field and dropdown
vocabulary directly; see their sections.

### Members that convert

Five label aliases change units on the way through. The Onboarding
conversion uses the same functions (`TmpCompat`), so an assignment in code
and a converted component agree. The setters approximate silently; the scan
is what names an approximation.

| TMP member | OneText property | Conversion |
|---|---|---|
| `lineSpacing` | `LineSpacing` | `multiplier = 1 + offset / 100`. TMP's `10` is `1.1`. A read is a float round trip: `10f` can come back `10.000002f`. |
| `alignment` (`TextAlignmentOptions`) | `Alignment` + `VerticalAlignment` | The bitfield is split across two axes. Reading gives Left and Right for `Start` and `End`. |
| `textWrappingMode` (`TextWrappingModes`), `enableWordWrapping` | `Wrap` | `Normal` and `PreserveWhitespace` become `Wrap`; `NoWrap` and `PreserveWhitespaceNoWrap` become `NoWrap`. Whitespace preservation is dropped. |
| `overflowMode` (`TextOverflowModes`) | `Overflow` | See below. |

| TMP value | Becomes |
|---|---|
| Alignment `Flush` | `Justified` (the last line is not stretched) |
| Alignment `GeoAligned` | `Center` |
| Alignment `Baseline`, `Midline` | `VerticalAlignment.Middle` |
| Alignment `Capline` | `VerticalAlignment.Top` |
| Overflow `Masking`, `Page` | `Truncate` |
| Overflow `ScrollRect`, `Linked` | `Overflow` |

`TextAlignmentOptions`, `TextWrappingModes` and `TextOverflowModes` are
declared in `OneText.UGUI` under TMP's names and values. A file that still
has `using TMPro;` will find those names ambiguous; qualify one, or let the
rewrite replace the `using`.

### Members that are compile errors on purpose

These exist on `OneTextLabel` as `[Obsolete(..., true)]`, so the compiler
prints the replacement instead of "no definition".

| Member | Use instead |
|---|---|
| `characterSpacing` | `<cspace=0.1em>` for a range, a `OneTextStyle` with Letter Spacing for the label, or the font asset's Letter Spacing. |
| `wordSpacing` | Nothing per space. Letter spacing, by the same three means, is the nearest. |
| `margin` | Inset the RectTransform, or parent the label to a rect of the wanted size. |
| `autoSizeTextContainer` | A `ContentSizeFitter`; the label reports `preferredWidth` and `preferredHeight`. |
| `havePropertiesChanged` | Nothing to read. Call `ForceMeshUpdate()` to force a rebuild. |
| `firstVisibleCharacter` | Nothing. A reveal starts at the start; paged text needs a string per page. |

Members that are simply absent, and fail as ordinary errors: `fontStyle`,
a lowercase `font` (the property is `Font`, of type `OneFontAsset`), and the
numeric `SetText("{0:2}", value)` overloads, whose format syntax is TMP's own.

### What the script rewrite does

It renames type names and replaces `using TMPro;`. It never changes a member
access, a string, a character literal or a comment.

| In source | Becomes |
|---|---|
| `TextMeshProUGUI`, `TMP_Text` | `OneTextLabel` |
| `TextMeshPro` | `OneTextMesh` |
| `TMP_InputField` | `OneTextInputField` |
| `TMP_Dropdown` | `OneTextDropdown` |
| `Dropdown`, in a file with `using UnityEngine.UI;` | `OneTextDropdown` |
| `TMP_TextInfo`, `TMP_CharacterInfo`, `TMP_LineInfo`, `TMP_WordInfo`, `TMP_LinkInfo`, `TMP_MeshInfo` | `OneTextTextInfo`, `OneTextCharacterInfo`, `OneTextLineInfo`, `OneTextWordInfo`, `OneTextLinkInfo`, `OneTextMeshInfo` |
| `TMP_VertexDataUpdateFlags` | `OneTextVertexDataUpdateFlags` |
| `TMPro_EventManager` | `OneTextEvents` |
| `TMPro.`-qualified forms of the above and of the three enums | the `OneText.`-qualified name |
| `using TMPro;` | `using OneText.UGUI;`, plus `using OneText;` when the file gained a `OneTextMesh` |

What it leaves:

- Every other TMP name (`TMP_FontAsset`, `TMP_SpriteAsset`, `TMP_Settings`
  and so on). These are counted as left over and listed with line numbers.
- A whole file, when removing `using TMPro;` would leave a bare TMP name with
  nowhere to resolve. The file is marked refused and returned unchanged.
- `Text`, `InputField` and `TextMesh`. The names are too common to rewrite.
- Files under `Plugins`, `ThirdParty`, `Standard Assets` and similar folders
  start unticked.
- Assembly definitions. A file inside one that does not reference `OneText`,
  `OneText.UGUI` or `OneText.Mesh` will not compile after the rewrite; the
  tab lists these with an **Add references** button.

One consequence of the map: `TMP_Text` becomes `OneTextLabel`, and a
`OneTextMesh` is not a `OneTextLabel`. A `TMP_Text` field that pointed at a
world `TextMeshPro` has to be redeclared as `OneTextMesh` by hand.

## Rich text

Tags OneText parses. Names are case-insensitive, an unclosed tag runs to the
end of the text, and a tag that is unknown or malformed is printed as typed.

| Tag | Argument |
|---|---|
| `<b>` `<i>` `<u>` `<s>` `<nobr>` `<sup>` `<sub>` | none |
| `<br>`, `<noparse>` | none |
| `<color=…>` | `#rgb`, `#rgba`, `#rrggbb`, `#rrggbbaa`, or one of black, white, red, green, blue, yellow, orange, purple, grey, cyan, magenta, brown, pink, clear |
| `<alpha=#80>` | two hex digits |
| `<mark>`, `<mark=…>` | optional colour |
| `<size=…>` | a number or a percentage (`150%`). A signed delta (`+4`) parses only inside an absolute `<size>`. An `em` value does not parse. |
| `<voffset=…>` `<cspace=…>` `<mspace=…>` | ems. The `em` suffix is optional, so a bare number is ems too. |
| `<align=…>` | left, right, center, justified, start, end |
| `<font=name>` | a font asset in the label's named fonts, by asset name |
| `<style=name>` | a `OneTextStyle` in the label's named styles, by asset name |
| `<link=id>` | an id, raised through the label's `LinkClicked` event |
| `<sprite=3>`, `<sprite=name>`, `<sprite name="name">` | an index or a sprite name in the label's `OneTextSpriteSheet`. The `name=` form is TMP's own spelling. It is read from 0.5.0 on; in 0.4.2 and earlier it prints literally, and `<sprite=name>` is the form to write there. |
| `<outline>` `<shadow>` `<glow>` | optional colour and parameters, e.g. `<outline=black w=0.6>` |
| `<ruby=reading>` | the annotation text |
| `<josa=…>` | a Korean particle, in either of its two forms; the right one is written for the text before it |
| `<wait=0.5>` | seconds; pauses a reveal |
| `<wave>` `<shake>` `<wobble>` `<bounce>` `<rainbow>` `<pulse>` `<fade>` `<rise>` `<swell>` `<glitch>` `<stretch>` `<flash>` `<pop>` `<drop>` | optional `amp=`, `freq=`, `speed=`, `for=` |

TMP tags that print literally. The scan reports each as `unsupported-tag`.

| TMP tag | Nearest replacement |
|---|---|
| `<margin>` `<margin-left>` `<margin-right>` | Inset the RectTransform. There is no per-paragraph margin. |
| `<indent>` `<line-indent>` | None. |
| `<line-height>` | The label's `LineSpacing`, for the whole label. None per line. |
| `<pos>` `<space>` `<width>` | None. |
| `<rotate>` | None. |
| `<gradient>` | None. |
| `<uppercase>` `<lowercase>` `<allcaps>` | Change the string. |
| `<smallcaps>` | None. |
| `<page>` | A string per page. |
| `<material>` | `<outline>`, `<shadow>` and `<glow>` cover what a material preset is usually switched for. Nothing else. |
| `<quad>` | None. |
| `<sprite index=…>` `<sprite anim=…>` | `<sprite=3>` for a fixed sprite. No animated sprites. |

Five more differences the scan does not flag:

- `<sprite name="x" tint=1>` draws the sprite, but only the name is read.
  Whatever follows it (`tint=`, `color=`) is ignored.
- `<voffset>`, `<cspace>` and `<mspace>` read a bare number as ems. TMP reads
  a bare number as pixels, so `<cspace=10>` is ten ems here. Rewrite such
  values in ems.
- `<font="A" material="B">` does not parse and prints literally. `<font=A>`
  parses when a font asset named `A` is in the label's named fonts.
- `<style=H1>` prints literally unless a `OneTextStyle` asset of that name is
  in the label's named styles. TMP's style sheet is not converted.
- The conversion does not create a sprite sheet. Assign a
  `OneTextSpriteSheet` to the label for `<sprite>` to draw.

## Fonts

OneText rasterises glyphs from the font file at run time. There is no atlas
to bake, and a TMP font asset's baked atlas is not something it can read. A
TMP font asset is useful only for the file it was made from.

The conversion finds that file through the asset's source font reference,
then the source file GUID a baked asset keeps, then two older editor fields.
The path has to end in `.ttf`, `.otf` or `.ttc`. For each distinct file it
makes one `OneFontAsset` beside it, named `<file name> Font.asset`, and reuses
one that is already there. The font asset's own fallback table becomes the
label's fallback list, tried before the project's.

`font-source-missing` means none of that produced a file: the asset was made
from an OS font, the source was deleted, or (for a legacy `Font`) it is one
of the editor's built-in fonts. The conversion then makes a **placeholder**
`OneFontAsset` under `Assets/OneText/Recovered Fonts`, one per missing
typeface, and points every label that wanted it there. Until it is filled
those labels draw in the project default. Import the font file anywhere in
the project and the placeholder waiting for that file name or family fills
itself; a log line says which. The tab lists these under **What you have to
fix by hand**, one row per typeface.

The project's defaults live in a `OneTextSettings` asset, edited in *Project
Settings > OneText*: a default font, an ordered fallback chain, and a switch
for falling back to the device's own fonts, which is on by default. When
converting from the tab, TMP's default font asset and global fallbacks are
copied into these if, and only if, the OneText ones are empty
(`project-default`).

## Materials and effects

TMP keeps outline, underlay and glow on a material, so each look is a
material preset. OneText keeps them on the label as a `TextDecoration`
(`OneTextLabel.Decoration`), packed into vertex channels, and draws every
label through one material. A preset becomes values on each label that used
it.

What the conversion reads from the material a label draws with:

| TMP | OneText | Condition |
|---|---|---|
| Outline width, colour, softness | Outline | Width above zero and a non-transparent colour; on shaders that declare `OUTLINE_ON`, the keyword must be set. |
| Face dilate and the normal weight | Face dilate | Always. |
| Underlay colour, offset, softness | Shadow | `UNDERLAY_ON` set and a non-transparent colour. |
| Glow colour, outer, inner | Glow | `GLOW_ON` set and a non-transparent colour. |

Values are scaled by the material's scale ratios and by the difference
between the two renderers' distance-field widths at the label's current size,
not copied.

What `material-effect` reports as lost or changed:

- An inner underlay (`UNDERLAY_INNER`). OneText's shadow is always outside.
- Underlay dilate. The shadow is drawn at the face's weight.
- A glow power other than 1. OneText's glow falls off linearly.
- A face colour other than white. It is not folded into the label's colour;
  set the colour to what you see.
- An outline wider than the distance field reaches. It is clamped and drawn
  thinner than TMP drew it.
- On an auto-sizing label (note): the effects are converted for the current
  size and drift in proportion at a very different one.

Nothing else on the material is read or reported: bevel and lighting, face
and outline textures, the glow offset. The outline's alpha is not carried
either; the label's alpha fades the outline with the text.

## Input fields

`OneTextInputField` composes with the platform input method: Korean, Japanese
and Chinese composition is drawn inline at the caret with an underline, is
not part of `text` until committed, and is committed rather than dropped when
focus leaves. On Android and iOS the field hands editing to the system
keyboard and mirrors its buffer.

**Carried:** the text, single or multiple lines, read-only, character limit,
caret colour, width and blink rate, the text component, placeholder, viewport
and target graphic, `Selectable`'s navigation, transition and colours, and
the persistent listeners on `onValueChanged`, `onSubmit` and `onEndEdit`.

**In code:** `text`, `caretPosition`, `stringPosition`,
`selectionAnchorPosition`, `selectionFocusPosition`, `isFocused`, `readOnly`,
`multiline`, `lineType`, `characterLimit`, `textComponent`, `textViewport`,
`placeholder`, `onValueChanged`, `onSubmit`, `onEndEdit`, `onSelect`,
`onDeselect`, `ActivateInputField`, `DeactivateInputField`,
`SetTextWithoutNotify`, `MoveTextStart`, `MoveTextEnd`, `selectionColor`,
`caretColor`, `caretWidth`, `caretBlinkRate` and `onFocusSelectAll` all exist.

The gaps:

- **There is no content type and no character validation. A password field
  converts to a plain field and draws the password in clear text.** An
  integer or email field stops validating. The scan reports each one as
  `no-counterpart`. Filter the value yourself on `onValueChanged`, and do not
  ship a converted password field without replacing the masking.
- Rich text is off inside a field. What is typed is text, not markup.
- A field is horizontal only. Vertical editing is not implemented.
- `MultiLineSubmit` becomes multiline with Return inserting a newline.
- `onSelect` and `onDeselect` exist, but listeners wired to them in the
  inspector are not read by the conversion. Nor is the selection colour.
- `onFocusSelectAll` selects on Tab or `ActivateInputField`, not on a click.
- There is no WebGL text input path yet.

## Dropdowns

`OneTextDropdown` has the shape of Unity's dropdown, with the two label
fields typed `OneTextLabel`. The conversion carries the options (text, image
and, from TMP, colour), the value, the template, the caption and item
images, the `onValueChanged` listeners and `Selectable`'s state, and
re-points the caption and item labels after they are converted. In code,
`options`, `value`, `onValueChanged`, `template`, `captionText`,
`captionImage`, `itemText`, `itemImage`, `Show`, `Hide`, `RefreshShownValue`,
`AddOptions`, `ClearOptions` and `SetValueWithoutNotify` mean what they mean
on Unity's.

Not there:

- **Multi-select.** It selects one option. Reported as `no-counterpart`.
- **Placeholder.** The first option shows when nothing is selected, and a
  stored value of -1 becomes 0. Reported as `no-counterpart`.
- **The fade.** `alphaFadeSpeed` is kept and carried but inert: the list
  opens and closes immediately.

## World text

`OneTextMesh` runs the same pipeline through a MeshFilter and MeshRenderer,
with no Canvas and no uGUI dependency. Font sizes are in points on TMP's
world scale, ten points to one local unit, so a `TextMeshPro` at size 36 in a
20×5 rect keeps its numbers and its size. `<size=44>` converts the same way;
em-relative values are unitless and unchanged.

It is deliberately the smaller component. It has no reveal or animation, no
decorations (outline, shadow, glow, underline), no inline sprites, no style
assets and no interaction. Decoration and animation tags are consumed and
draw nothing; `<sprite=3>` leaves a gap its own width; `<style=…>`,
`<font=…>` and `<sprite=name>` print literally, because the component has no
lists to resolve them against. The scan reports sprite and animation tags on
world text as `no-counterpart`.

An outline, underlay or glow on a world text's material is not carried, and
no finding is raised for it. If the look depends on one, check each converted
world text by eye.

## After converting

- Scan again. The counts should only go down.
- Clear every error. Read the warnings once; switch on *Show notes* once.
- Fill the placeholders under **What you have to fix by hand**.
- Check *Project Settings > OneText* for the default font and fallbacks.
- Replace masking and validation on every former password, email or number
  field.
- Open the screens with outlined or shadowed text and compare.
- Edit the strings named by `unsupported-tag`, including strings that live in
  localisation tables, which the component scan does not read.
- Rewrite the scripts, let Unity recompile, fix what the compiler points at,
  and convert again for anything held back by `reference-would-break`.
- Enter Play mode and look for fields reading None.
- Remove TextMesh Pro only when a scan finds nothing and nothing refers to
  `TMPro`. On `com.unity.ugui` 2.0.0 and newer it is part of that package.

## Troubleshooting

**Most labels are held back with `reference-would-break`.** Scripts still
declare their fields as TMP types. Run Step 2, recompile, convert again.

**A field reads None in a file the run did not list.** With *Fix references
that cross from one scene or prefab into another* off, nothing mends or
reports those. Turn it on. With it on, an unmended field is an error under
`cross-container-reference` that names the field.

**A prefab reports `unsaveable-container` or `save-failed`.** The usual cause
is a missing script, which asset packs often leave behind. Remove or restore
it and convert again.

**Converted text draws in the wrong face.** Its font is a placeholder waiting
for a file, or it has none and is drawing in the project default or a system
font. The console names each missing font once.

**A script file is refused.** It uses a TMP type with no counterpart, and
removing `using TMPro;` would break it. Deal with the names listed, then scan
again. Files that share a type or an extension method with it are grouped,
and the group is held back together.

**Errors like `CS0246` after the rewrite.** The file sits in an assembly
definition that does not reference OneText. Use **Add references**.

**An outline came out thinner, or shifts with size.** See `material-effect`.
The distance field has a fixed reach, and converted values are exact only at
the size the label had when it was converted.

**The project uses DOTween.** The package has an integration assembly,
`OneText.Integrations.DOTween`, compiled when `ONETEXT_DOTWEEN` is defined;
the editor adds and removes that symbol for the selected build target as
DOTween comes and goes. `DOColor`, `DOFade`, `DOFontSize`, `DOText`,
`DOCounter` and `DOMaxVisibleCharacters` exist for `OneTextLabel`. The
Onboarding tab lists what the integration cannot cover.
