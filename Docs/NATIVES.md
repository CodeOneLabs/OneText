# Native binaries

OneText links one native library, HarfBuzz, and ships a prebuilt copy for
every platform it supports. This file records where those copies come from,
what was checked before they were committed, and how to replace them.

## Where they come from

All of them are from the `HarfBuzzSharp.NativeAssets.*` NuGet packages:
Microsoft's builds for SkiaSharp, MIT licensed, one build tree, one HarfBuzz
version across every platform. Building HarfBuzz five ways ourselves would be
five toolchains to keep working and five chances for the platforms to drift
apart in ways nobody notices until a script stops joining on one of them.

Current version: **14.2.1.1** (HarfBuzz **14.2.1**).

| Platform | Package | Source path in the package |
|---|---|---|
| macOS | `HarfBuzzSharp.NativeAssets.macOS` | `runtimes/osx/native/libHarfBuzzSharp.dylib` |
| Windows x64 | `HarfBuzzSharp.NativeAssets.Win32` | `runtimes/win-x64/native/libHarfBuzzSharp.dll` |
| Windows x86 | `HarfBuzzSharp.NativeAssets.Win32` | `runtimes/win-x86/native/libHarfBuzzSharp.dll` |
| Windows ARM64 | `HarfBuzzSharp.NativeAssets.Win32` | `runtimes/win-arm64/native/libHarfBuzzSharp.dll` |
| Linux x64 | `HarfBuzzSharp.NativeAssets.Linux` | `runtimes/linux-x64/native/libHarfBuzzSharp.so` |
| Android arm64-v8a | `HarfBuzzSharp.NativeAssets.Android` | `runtimes/android-arm64/native/libHarfBuzzSharp.so` |
| Android armeabi-v7a | `HarfBuzzSharp.NativeAssets.Android` | `runtimes/android-arm/native/libHarfBuzzSharp.so` |
| Android x86_64 | `HarfBuzzSharp.NativeAssets.Android` | `runtimes/android-x64/native/libHarfBuzzSharp.so` |
| iOS device + simulator | `HarfBuzzSharp.NativeAssets.iOS` | `runtimes/ios/…` and `runtimes/iossimulator/…`, repacked into one `.xcframework` |

Linux ships x64 only, because that is the only Linux target Unity's standalone
player builds for. Web is the exception to the whole table: NuGet has no wasm
build, so that one is compiled here, from source, and has its own section
below.

Windows ARM64 is tagged `StandaloneWindows64` with `CPU: ARM64`, which is a
Unity-6-era concept. The package still claims 2021.3 LTS, and no 2021.3 editor
has imported these files; if an older editor cannot tell the two Win64
binaries apart by CPU it will refuse the build, and that would be the first
thing to try removing.

## What was checked before committing

Every binary, at vendor time:

- **`hb_shape` and `hb_font_draw_glyph` are exported.** Shaping and outlines
  are the two things the engine cannot do without.
- **31 `hb_subset_*` symbols are exported.** `harfbuzz-subset` is a separate
  library in HarfBuzz's build, so a binary can be a perfectly good shaper and
  still have no subsetting. If one platform dropped it, subsetting would become
  a feature that exists or not depending on which platform loaded, which is
  worse than not having the feature. `HarfBuzzSubset.IsAvailable` asks the same question at
  runtime, and `NativesTests` asserts it for the host.
- **Android 64-bit libraries are 16 KB page aligned** (`PT_LOAD` alignment
  `0x4000`). Google Play requires this of 64-bit libraries, and the rejection
  arrives at submission rather than at build. Unity records what it found as
  `Is16KbAligned` in the plugin's `.meta`, and `NativesTests` asserts it.

## The name in `DllImport`

`HarfBuzzApi.Lib` is `"libHarfBuzzSharp"`, with the `lib` spelled out, on
every platform. This looks redundant and is not. il2cpp's POSIX library loader
walks prefix and suffix variations, so on macOS, Linux and Android a bare
`"HarfBuzzSharp"` finds `libHarfBuzzSharp.dylib`/`.so` and everything works.
The Windows loader's `ProbeForLibrary` opens exactly the name it is given, so
`"HarfBuzzSharp"` looks for `HarfBuzzSharp.dll`, never finds
`libHarfBuzzSharp.dll`, and throws `DllNotFoundException` in the Windows editor
and in both player backends. SkiaSharp spells out the `lib` for the same
reason.

`__Internal` is wrong here too, for iOS: that names a library linked into the
executable, and this package ships iOS as a dynamic framework because the NuGet
family has no static build. Both mistakes compile cleanly and fail at the first
shaping call, on one platform.

## Three modifications, and why

The iOS binaries are not committed byte-for-byte:

1. **Thinned to arm64.** The package's device framework is a fat binary of
   arm64 plus a legacy `x86_64` slice built against the device SDK. The modern
   simulator framework sits beside it and is what simulator builds should use,
   so the extra slice is 2.8 MB of nothing.
2. **`Info.plist` corrected.** Microsoft's device framework ships a *simulator*
   `Info.plist`, with `CFBundleSupportedPlatforms: [iPhoneSimulator]` and
   `DTPlatformName: iphonesimulator`. Left alone, that framework fails device
   install and App Store validation. The keys are set to `iPhoneOS` /
   `iphoneos`.
3. **Repacked as one `.xcframework`.** The device and simulator frameworks
   have the same name, and Unity's plugin importer has no device/simulator
   switch to keep two same-named plugins apart on one platform, so shipping
   them as two plugins is the collision this whole file warns about. An
   `.xcframework` is the format Apple made for exactly this: one bundle,
   `ios-arm64` and `ios-arm64_x86_64-simulator` inside it, and Xcode picks.
   The manifest is hand-written (`xcodebuild -create-xcframework` needs full
   Xcode); it is a documented plist, and `NativesTests` checks both slices are
   present and that the manifest distinguishes them.

The `_CodeSignature` directories are removed from both frameworks: thinning
invalidates the signature, and Xcode re-signs embedded frameworks at build time
regardless.

## Re-vendoring

```bash
V=14.2.1.1
for p in win32 linux android ios macos; do
  curl -sLO "https://api.nuget.org/v3-flatcontainer/harfbuzzsharp.nativeassets.$p/$V/harfbuzzsharp.nativeassets.$p.$V.nupkg"
  unzip -qo "harfbuzzsharp.nativeassets.$p.$V.nupkg" -d "$p"
done
```

Copy the files to the paths in `Runtime/Plugins/` above, redo the three iOS
modifications, then let Unity write the import settings rather than editing
`.meta` YAML by hand:

```
Unity -batchmode -nographics -quit -projectPath <dev-project> \
      -executeMethod OneText.Editor.NativePluginSettings.ApplyBatch
```

Every one of these files is called `libHarfBuzzSharp`, and Unity refuses a
build when two plugins of the same name are enabled for one platform, which is
exactly what a hand-written platform mask gets wrong. `NativesTests` fails if a
binary is missing, marked Any Platform, tagged for the wrong CPU or editor OS,
enabled for a platform that is not its own, not 16 KB aligned, or (on iOS) not
embedded or missing a slice.

## Web (WebGL): the one we build ourselves

`Runtime/Plugins/WebGL/libHarfBuzzSharp.a` is 3.8 MB, a GNU archive of
WebAssembly object files, built by `Tools/build_webgl_natives.sh`. It is the
only binary in this package that is not vendored, because the HarfBuzzSharp
NuGet family has no wasm build and there is nothing to vendor.

### The version match, which is the whole problem

Unity links plugin archives with **its own bundled Emscripten**, and LLVM makes
no promise that one version's object files can be read by another's linker. The
editor's version is documented, and is not per-patch:

| Unity | Emscripten |
|---|---|
| **2023.2 and later (every Unity 6, including 6000.0, 6000.1 and 6000.3)** | **3.1.38-unity** |
| 2022.2 and later | 3.1.8-unity |
| 2021.2 and later | 2.0.19.6-unity |

So the archive is built with upstream emsdk **3.1.38**, the release Unity's fork
is cut from (clang 17.0.0). The exact copy in the editor is at
`.../PlaybackEngines/WebGLSupport/BuildTools/Emscripten/emscripten/emscripten-version.txt`,
and if that ever stops saying `3.1.38` the `.a` here is stale: change
`EMSDK_VERSION` at the top of the build script and rebuild. Do not ship the old
one and hope. A mismatch is not a subtle bug; it is a link error in the Unity
build, which is the good kind, but only if somebody rebuilds instead of
downgrading the editor.

The archive is built **without `-pthread`**. Unity's Web player is
single-threaded unless the project opts into pthreads, and that flag has to
match across the whole link.

### The trim, and the presets we could not use

The milestone called for HarfBuzz's tiny-build options. They cannot be used as
written. `HB_TINY` expands to `HB_LEAN` + `HB_MINI`, and `HB_LEAN` defines
`HB_NO_DRAW`, `HB_NO_COLOR`, `HB_NO_VAR`, `HB_NO_BITMAP` and `HB_NO_METRICS`,
every one of which is something `HarfBuzzApi.cs` calls. `hb_font_draw_glyph` is
how the engine gets glyphs at all; `hb_ot_color_*` is COLRv0, CPAL and CBDT
PNGs; `hb_ot_var_*` and `hb_font_set_variations` are variable fonts;
`hb_font_get_h_extents` is line metrics. A `HB_TINY` build would compile and
then fail to link against half the P/Invoke surface.

`HB_MINI` is worse in a quieter way. It adds `HB_NO_AAT` and `HB_NO_LEGACY`,
which remove no API at all: they remove *shaping behaviour*. A player built
that way would shape `morx` fonts and legacy-cmap symbol fonts differently from
every other platform, silently, and that is precisely the drift the rest of this
file exists to prevent.

So the trim is hand-picked, under one rule: **remove entry points
`HarfBuzzApi.cs` never names, and infrastructure a browser cannot reach; never
remove anything that changes what `hb_shape` returns.**

- Infrastructure: `HB_NO_MT`, `HB_NO_MMAP`, `HB_NO_OPEN`, `HB_NO_GETENV`,
  `HB_NO_SETLOCALE`, `HB_NO_ERRNO`, `HB_NO_ATEXIT`, `HB_DISABLE_DEPRECATED`,
  `HB_NDEBUG`/`NDEBUG`.
- Unused API: `HB_NO_BUFFER_MESSAGE`, `HB_NO_BUFFER_SERIALIZE`,
  `HB_NO_BUFFER_VERIFY`, `HB_NO_LAYOUT_COLLECT_GLYPHS`,
  `HB_NO_LAYOUT_FEATURE_PARAMS`, `HB_NO_MATH`, `HB_NO_META`, `HB_NO_STYLE`,
  `HB_NO_SVG`.
- Size: `HB_OPTIMIZE_SIZE` and `-Oz`. `HB_OPTIMIZE_SIZE_MORE` and
  `HB_MINIMIZE_MEMORY_USAGE` are deliberately *not* set: they buy bytes with
  shaping speed, and this is a text engine.

There is no CMake step. The script compiles one translation unit
(HarfBuzz's own `src/harfbuzz-subset.cc`, which amalgamates the core *and* the
subsetter) straight to a single object, and archives that. Unity imports one
file per plugin, and every other platform here is one file called
`libHarfBuzzSharp`, so one object is also the shape the rest of this document
already describes. The files `harfbuzz.cc` has that this one lacks are the
platform integrations (CoreText, DirectWrite, FreeType, GDI, glib, graphite2,
Uniscribe), none of which exist on Web. Why it is one object and not two
merged archives is the first half of "The Unity Web build" below, and it is not
a stylistic choice.

### `__Internal`, which is right exactly once

`HarfBuzzApi.Lib` is `"libHarfBuzzSharp"` everywhere except under
`#if UNITY_WEBGL && !UNITY_EDITOR`, where it is `"__Internal"`. This is the same
name the iOS section above calls a mistake, and the difference is real: on iOS
the binary is a dynamic framework resolved by name, and here Emscripten has
linked the archive *into the module* before any managed code runs. The
`!UNITY_EDITOR` half matters: `UNITY_WEBGL` is defined in the editor too
whenever Web is the active build target, and in play mode the editor is still
loading the macOS dylib.

Beside it is `HbPrefix`, `"onetext_"` on Web and `""` everywhere else, and every
one of the 58 `DllImport`s names its `EntryPoint` explicitly as
`HbPrefix + "hb_…"`, string concatenation of constants, which an attribute
accepts. On the other nine platforms the prefix is empty and each attribute
names exactly the symbol the method name already bound to, so nothing about them
changes; the EditMode suite passing unchanged on macOS is the check on that.
Why the prefix exists at all is the collision described below.

### What was verified, and how

The three questions this file asks of every vendored binary, asked of an archive
instead of a shared library, by the build script itself: `hb_shape` and
`hb_font_draw_glyph` are defined, and **31 `hb_subset_*` symbols** are present,
the same count as every other platform. Page alignment has no meaning here.

Beyond that, this is the first native in this package that has actually been
*run* somewhere other than macOS. `Web~/` is a browser harness: the same
archive linked into a `MODULARIZE` module, exporting exactly the P/Invoke
surface of `HarfBuzzApi.cs`, driven from JavaScript. It shapes real Noto fonts,
pulls outlines through `hb_font_draw_glyph` with draw callbacks passed the way
C# passes delegates, and draws the result twice: once through WebGL2 and once
through WebGPU, from byte-identical vertex data.

In Chromium on an Apple M4 Pro, **10 of 10 shaping assertions passed**, both
renderers drew, and the console was clean:

| assertion | observed |
|---|---|
| Arabic joining, `مرحبا` | 5 cp → 6 glyphs; 6 contextual forms; clusters `4 3 3 2 1 0` (RTL visual order); 1 zero-advance mark |
| Arabic lam-alef, `لا` | two pieces measuring 582 units, exactly the precomposed U+FEFB |
| Arabic GSUB | `init`, `medi`, `fina` present among 16 features |
| Devanagari `क्षत्रिय` | 8 cp → 4 glyphs |
| Devanagari `कि` | i-matra emitted *before* its consonant, as a width-matched variant |
| Latin kerning | 5 kerned pairs: AV −40, Ta −80, Wa −20, AT −70, Yo −50 |
| Emoji ZWJ family | 5 cp → 1 glyph; CBDT PNG present |
| `hb_draw` | 317 path commands, 209 of them curves |
| `hb_font_get_h_extents` | asc 1069, desc −293, upem 1000 |
| `hb_subset_or_fail` | 621 572 → 5 944 bytes |
| WebGL2 | ANGLE Metal, Apple M4 Pro; 23 glyph quads |
| WebGPU | apple metal-3; 23 glyph quads, no uncaptured errors |

The two canvases were also compared pixel for pixel: both carry ink over ~5.5%
of their area, and 1.3% of pixels differ by more than 8/255: edge filtering
between a GL driver and a WebGPU one, nothing structural.

Two assertions had to be *weakened* against reality, which is worth recording
because both are the obvious thing to assume and both are wrong. Arabic does
**not** produce fewer glyphs than codepoints: joining is a one-to-one
substitution, and Noto Sans Arabic splits dots off through `ccmp`, so the count
goes *up*. And pre-base reordering cannot be caught by watching cluster
numbers: HarfBuzz merges the cluster when it moves a glyph, so `क्षत्रिय`
returns clusters `0 3 3 7`, rising, betraying nothing.

One engine feature is a no-op on Web and cannot be otherwise: system-font
fallback (`SystemFonts`) finds the operating system's own faces by walking the
platform's font directories, and a browser has no font directory to walk. On
Web the tier returns nothing, a character no bundled font covers stays a box,
and Doctor (which runs in the editor, on a machine that does have fonts)
reports it under `system-fallback` rather than `tofu`. Bundle a font for every
script a Web build ships.

The harness needs the coverage fonts, which are not committed
(`Tools/fetch_coverage_fonts.py`, and `Tests/CoverageFonts~/` is gitignored):

```bash
python3 Tools/fetch_coverage_fonts.py
Tools/build_webgl_natives.sh
python3 -m http.server 8712      # from the repository root
# then open http://127.0.0.1:8712/Web~/index.html
# window.__onetext.runTests() resolves to the full result object
```

### The Unity Web build, and what it found

A real Web player has now been built and run, with editor **6000.0.77f1** and
its Web build support module, from a copy of the dev project. It took two
failures to get there. Both are fixed; the second one is the interesting one,
and it is the reason the Web archive does not use HarfBuzz's own symbol names.

The editor's `emscripten-version.txt` reads **`3.1.39-git`**, not the
`3.1.38-unity` the manual documents for 2023.2 and later: the docs give a
per-branch version, and the shipped module is a later trunk snapshot. It does
not matter: both are clang 17.0.0, Unity's own `llvm-nm` reads all 506 `hb_*`
symbols out of our 3.1.38 archive, and nothing in any link failed on object
format. The version table above is still the thing to check; it is just not
exact to the patch.

`NativePluginSettings.ApplyBatch` was extended with a WebGL target and run. It
agreed with the hand-written `.meta` except for one line, `CPU: AnyCPU` under
`WebGL`, which it added, and it left the other nine platforms untouched.

**First failure, ours.** The first build stopped on
`wasm-ld: error: duplicate symbol`, and the duplicates were inside our own
archive: it merged `libharfbuzz.a` and `libharfbuzz-subset.a`, which overlap.
The build script now compiles the single `harfbuzz-subset.cc` amalgamation
instead, and checks for duplicate symbols before it writes anything. That also
took the archive from 7.4 MB to 3.8 MB.

### Unity's Web player already contains a HarfBuzz

The second failure is not ours.
`WebGLSupport/BuildTools/lib/modules/WebGLSupport_UnityPlayer.TextRenderingModule_Dynamic.a`
contains `harfbuzz_5sk4y.o`: **Unity statically links its own HarfBuzz, version
8.0.1, into every Web player**, alongside FreeType, libpng, zlib and ICU. Its
symbols and ours are the same names, so wasm-ld sees **591 strong symbols twice**
and stops.

Three ways out were tried. Two do not work, and the measurements are worth
keeping because each looks plausible until it is run:

- **Strip the module out.** `stripEngineCode` with
  `ManagedStrippingLevel.High` does not drop it: any Canvas pulls in
  `UnityEngine.UI`, which references `Font`, which keeps TextRendering alive.
  Duplicates unchanged.
- **Rename our symbols in the object afterwards.** `llvm-objcopy
  --redefine-syms` refuses outright: *"only flags for section dumping, removal,
  and addition are supported"*. It has no symbol-table support for wasm objects,
  so there is nothing to rewrite after the compiler has run.
- **Do not ship ours; bind `__Internal` to Unity's.** This links, with zero
  duplicates, and then fails with **18 undefined symbols, every one an
  `hb_subset_*`**. Unity builds from `External/harfbuzz/src/harfbuzz.cc`, the
  core amalgamation, so its copy is a complete shaper with no subsetting at
  all, exactly the case the "What was checked" section says a binary can be,
  discovered on the one platform where the binary is not ours. Dropping the
  subset calls makes it build and run, on HarfBuzz 8.0.1. That was measured and
  rejected: it puts the web six major versions behind every other platform and
  loses subsetting, which is the drift this whole file exists to prevent.

### The fix: our HarfBuzz answers to different names

Since the rename cannot happen after compiling, it happens before. Every
HarfBuzz identifier is `#define`-d to an `onetext_`-prefixed one, through a
header force-included with `-include`, so Unity keeps `hb_shape` and we get
`onetext_hb_shape`. The two sit side by side in one player.

The renaming is done **by identifier, not by symbol name**, and that is the part
that makes it tractable. Rename the identifier `hb_parse_int` and the mangled
`_Z12hb_parse_int…` becomes `_Z20onetext_hb_parse_int…` by itself; rename the
*type* `hb_font_t` and every mangled name that mentions it moves too. So the
C++ internals (`_hb_NullPool`, `AAT::hb_aat_apply_context_t`,
`hb_subset_plan_t::…`) are covered by the same list as the C API, without
enumerating them. The identifiers are recovered from the length prefixes in the
mangled names (`_Z12hb_parse_int` = twelve characters), not by regex, which
would run them together into `hb_font_tP11hb_buffer_t` and rename nothing.

The build script does two compiles: one to collect symbols, then the generated
header, then the real one. **977 identifiers** get renamed. Three edge cases
needed hand-holding, all of them recorded in the script:

- `OT::cff1`'s lookup tables are not `hb_`-prefixed, so the *namespaces*
  `cff1`, `cff2` and `graph` are renamed instead, which moves everything
  inside them.
- `hb_color_get_{alpha,red,green,blue}` and `hb_glyph_info_get_glyph_flags` are
  shadowed by function-like macros of the same name, and a macro in a header
  beats an object-like `#define` that arrived earlier. The macro cannot be
  deleted either: the functions are written `return hb_color_get_blue (color);`
  on the assumption that the macro catches that call, so removing it turns all
  five into infinite recursion, which the compiler duly warned about when it
  was tried. What moves instead is the *definition*, `(hb_color_get_blue)
  (hb_color_t color)`, parenthesised precisely so the macro cannot reach it.
- That leaves five definitions with no declaration, and `hb.hh` turns
  `-Wmissing-prototypes` into an error from inside the source, where a
  command-line `-Wno-` cannot reach it. It is downgraded to a warning.

The script then **refuses to write an archive that still collides**: it checks
that no strong symbol is still named `hb_*`, and, when a Unity Web module is
installed, runs the exact comparison wasm-ld will run, against the editor's own
`TextRenderingModule`. Result: `no strong symbol collides with
WebGLSupport_UnityPlayer.TextRenderingModule_Dynamic.a`.

`Web~/onetext-hb.{js,wasm}` is built from the *unrenamed* object on purpose. The
harness links its own module and never meets Unity's HarfBuzz, so it has nothing
to collide with, and `Web~/hb.js` goes on reading like `HarfBuzzApi.cs` instead
of like this build script's private naming scheme.

### What the running player proved

Built from `Assets/Editor/WebProofBuild.cs` in the dev-project copy: a scene
built in code, four `OneTextLabel`s, fonts loaded as `TextAsset` bytes through
`SetFont`. Served over a static server and opened in Chromium on an Apple M4
Pro. Both builds succeeded with **0 errors, 0 warnings**, and the console was
clean apart from Unity's own startup logging.

| | WebGL2 | WebGPU |
|---|---|---|
| `PlayerSettings` graphics API | `OpenGLES3` | `WebGPU` (accepted by 6000.0.77f1) |
| device reported at runtime | `OpenGL ES 3.0 (WebGL 2.0 Chromium)` | `WebGPU 1.0 [1.0]` |
| build size | 45.4 MB | 45.7 MB |
| **`Shaper.HarfBuzzVersion`** | **`14.2.1`** | **`14.2.1`** |
| `FontSubsetter.IsAvailable` | `true` | `true` |
| a subset, actually run | 621 572 → **1 560 bytes** | 621 572 → **1 560 bytes** |
| Arabic `مرحبا` | 5 cp → **6 glyphs** | 5 cp → **6 glyphs** |
| Devanagari `क्षत्रिय` | 8 UTF-16 → **4 glyphs** | 8 UTF-16 → **4 glyphs** |
| Latin `AVATar Wave` | 11 glyphs, 1 line | 11 glyphs, 1 line |
| `family 👨‍👩‍👧😀` | 18 UTF-16 → **10 glyphs** | 18 UTF-16 → **10 glyphs** |

Two lines in that table are the whole point. **`14.2.1`** is the version *our*
archive reports, so `__Internal` resolved against the renamed symbols and not
against the 8.0.1 sitting beside them in the same binary; an earlier build,
before the renaming, returned `8.0.1` here and that is what it looks like when
Unity's copy wins. And **subsetting ran**, cutting Noto Sans from 621 572 bytes
to 1 560: that is the capability Unity's HarfBuzz does not have at all, so it
cannot have come from anywhere but ours.

The glyph counts are the same numbers the `Web~/` harness gets from the same
HarfBuzz: six glyphs from five Arabic letters because of the `ccmp` dot split,
four from eight Devanagari codepoints because of the conjuncts. They were also
unchanged from the 8.0.1 build, which is a small piece of evidence that the two
versions agree on these strings. Both players rendered joined RTL Arabic, a
Devanagari conjunct stack, kerned Latin and colour emoji.

One more thing a player build needs, unrelated to natives: **the SDF shader has
to reach the player.** Nothing in a scene references it (every label shares one
material this package builds at runtime), so the build's dependency walk never
meets the shader, strips it, and every label lays out perfectly and draws zero
glyphs with `OneText/SDF shader not found` in the console. The Web proof build
above worked around it by hand, through `GraphicsSettings`, and for a while the
docs asked every project to do the same. That was the wrong shape of fix: a
manual project setting nobody discovers until their own first build fails, in a
way that looks like a font problem.

The shader now lives at `Runtime/Shaders/Resources/OneText-SDF.shader` and
`SharedGlyphAtlas` loads it with `Resources.Load` rather than `Shader.Find`.
Anything under a `Resources` folder ships whether or not the dependency walk can
see who wants it (including a `Resources` folder inside a package), so the
folder name is what carries the dependency, and a player build carries the
shader with no project setting at all. `Shader.Find` stays behind the load for
projects that already added the Always Included Shaders line, and Doctor's
`sdf-shader` rule fails a build whose shader is under neither mechanism.

Proved by a player rather than by reading the manual. A release macOS build of
the dev project (one scene, one label, nothing of OneText's in Always Included
Shaders) puts `OneText/SDF` in `resources.assets` and in neither `level0` nor
`globalgamemanagers`, which is exactly the claim: no scene referenced it and no
project setting declared it, and it shipped anyway. The build log compiles both
its programs for Metal, and the running game says:

```
[proof] Resources.Load<Shader>("OneText-SDF") = 'OneText/SDF' supported=True
[proof] shared material = 'OneText SDF (shared)' shader='OneText/SDF' supported=True
[proof] label text='Shader shipped.' quads=14 materialForRendering='OneText/SDF'
[proof] lit pixels = 8742 of 480000
```

The last line is a readback of the player's own framebuffer: 8 742 pixels of an
otherwise black 800×600 window are glyphs, drawn on the GPU through that
material. `supported=True` is the half that a name lookup cannot fake: a
shader can be present and have no compiled variants for the platform.

The EntryPoint change touches all nine other platforms' declarations, so the
EditMode suite was run on macOS afterwards: **417 tests, 416 passed, 0 failed,
1 skipped**, unchanged. An empty `HbPrefix` names exactly the symbol each
method name already bound to.

### What is still untested

Only Web and macOS have been run. Windows, Linux, Android and iOS remain checked
but never executed, as above. Nor has the Web player been tried with pthreads
enabled, in a project that also uses TextMeshPro or Unity's legacy text, or on
any GPU other than Apple silicon.

The renaming is pinned to HarfBuzz 14.2.1 by more than the version number: the
namespace list (`cff1`, `cff2`, `graph`), the five macro-shadowed accessors and
the `hb.hh` pragma are all things a future HarfBuzz could move. The script's
collision check is what catches that; it compares against the editor's actual
`TextRenderingModule` and refuses to write an archive that would not link. Run
it after any version bump and believe the result rather than this paragraph.

## Licence

Two notices, both MIT, both at `Runtime/Plugins/` because they cover every
binary under it; these come from one build tree.

- `HarfBuzz-COPYING.txt`: HarfBuzz's own "Old MIT" licence and copyright
  holders (Behdad Esfahbod, Google, Red Hat, Mozilla, Facebook and others).
  This is the notice that has to travel with the binaries, because the binaries
  *are* HarfBuzz.
- `HarfBuzzSharp-LICENSE.txt`: the Xamarin/Microsoft MIT licence covering the
  packaging.

## What has not been verified

Only macOS has run. The other binaries are checked for everything checkable
without the platform (presence, architecture, exported symbols, page
alignment, import settings) and no further. Specifically untested: any
Windows, Linux, Android or iOS build; the Windows ARM64 rows on 2021.3; and
the iOS xcframework's Xcode integration, which is the piece with the most
moving parts.

CI has never run either: the repository has no remote yet. The Windows editor
job added for this milestone is marked `continue-on-error`, because game-ci's
test runner documents package testing as Linux-only and the job may simply not
work; it is there to be made to work, not to be relied on.
