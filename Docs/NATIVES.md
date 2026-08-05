# Native binaries

OneText links one native library, HarfBuzz, and ships a prebuilt copy for
every platform it supports. This file records where those copies come from,
what was checked before they were committed, and how to replace them.

## Where they come from

All of them are from the `HarfBuzzSharp.NativeAssets.*` NuGet packages —
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
player builds for. WebGL is absent on purpose: it is the one platform that
needs a real toolchain (Emscripten, matched to the editor's version) and it
gets its own milestone.

Windows ARM64 is tagged `StandaloneWindows64` with `CPU: ARM64`, which is a
Unity-6-era concept. The package still claims 2021.3 LTS, and no 2021.3 editor
has imported these files — if an older editor cannot tell the two Win64
binaries apart by CPU it will refuse the build, and that would be the first
thing to try removing.

## What was checked before committing

Every binary, at vendor time:

- **`hb_shape` and `hb_font_draw_glyph` are exported.** Shaping and outlines
  are the two things the engine cannot do without.
- **31 `hb_subset_*` symbols are exported.** `harfbuzz-subset` is a separate
  library in HarfBuzz's build, so a binary can be a perfectly good shaper and
  still have no subsetting. If one platform dropped it, subsetting would become
  a feature that exists or not depending on which platform loaded — worse than
  not having the feature. `HarfBuzzSubset.IsAvailable` asks the same question at
  runtime, and `NativesTests` asserts it for the host.
- **Android 64-bit libraries are 16 KB page aligned** (`PT_LOAD` alignment
  `0x4000`). Google Play requires this of 64-bit libraries, and the rejection
  arrives at submission rather than at build. Unity records what it found as
  `Is16KbAligned` in the plugin's `.meta`, and `NativesTests` asserts it.

## The name in `DllImport`

`HarfBuzzApi.Lib` is `"libHarfBuzzSharp"` — with the `lib` spelled out, on
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
   `Info.plist` — `CFBundleSupportedPlatforms: [iPhoneSimulator]`,
   `DTPlatformName: iphonesimulator`. Left alone, that framework fails device
   install and App Store validation. The keys are set to `iPhoneOS` /
   `iphoneos`.
3. **Repacked as one `.xcframework`.** The device and simulator frameworks
   have the same name, and Unity's plugin importer has no device/simulator
   switch to keep two same-named plugins apart on one platform — so shipping
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
build when two plugins of the same name are enabled for one platform — which is
exactly what a hand-written platform mask gets wrong. `NativesTests` fails if a
binary is missing, marked Any Platform, tagged for the wrong CPU or editor OS,
enabled for a platform that is not its own, not 16 KB aligned, or (on iOS) not
embedded or missing a slice.

## Licence

Two notices, both MIT, both at `Runtime/Plugins/` because they cover every
binary under it — these come from one build tree.

- `HarfBuzz-COPYING.txt` — HarfBuzz's own "Old MIT" licence and copyright
  holders (Behdad Esfahbod, Google, Red Hat, Mozilla, Facebook and others).
  This is the notice that has to travel with the binaries, because the binaries
  *are* HarfBuzz.
- `HarfBuzzSharp-LICENSE.txt` — the Xamarin/Microsoft MIT licence covering the
  packaging.

## What has not been verified

Only macOS has run. The other binaries are checked for everything checkable
without the platform — presence, architecture, exported symbols, page
alignment, import settings — and no further. Specifically untested: any
Windows, Linux, Android or iOS build; the Windows ARM64 rows on 2021.3; and
the iOS xcframework's Xcode integration, which is the piece with the most
moving parts.

CI has never run either: the repository has no remote yet. The Windows editor
job added for this milestone is marked `continue-on-error`, because game-ci's
test runner documents package testing as Linux-only and the job may simply not
work — it is there to be made to work, not to be relied on.
