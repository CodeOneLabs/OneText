# Runtime/Integrations/Addressables

`AddressablesFontSource`: an `IFontSource` that loads on-demand fonts (`OneTextSettings.OnDemandFonts`, see [Runtime/Core/Fonts](../../Core/Fonts/README.md)) by Addressables address instead of `Resources` path.

## Files

| File | Responsibility |
|---|---|
| `OneText.Integrations.Addressables.asmdef` | Compiles only with `ONETEXT_ADDRESSABLES`, which a version define sets when `com.unity.addressables` 1.17.0 or later is in the project. A project without Addressables never compiles this folder and gains no dependency. |
| `AddressablesFontSource.cs` | `Load` (`LoadAssetAsync`), `LoadNow` (`WaitForCompletion`, for the on-demand tier inside a layout pass), `Release` (`Addressables.Release` of one handle per load, last in first out). Registers itself with `FontResidency.RegisterSource` at `SubsystemRegistration`, so choosing Addressables in Project Settings > OneText is all a project does. |

## Invariants

- One handle per successful load, released exactly once. `FontResidency` pairs them, including the second handle a synchronous load can take while an asynchronous one for the same key is in flight.
- A failed load's handle is released at once.
- Keys default to the asset path (`OneFontReferences.KeyFor`), the address Addressables gives an entry unless it is renamed.

## Tests

None in the package: CI has no Addressables. The source contract is covered against a test source in `Tests/Editor/FontResidencyTests.cs`.
