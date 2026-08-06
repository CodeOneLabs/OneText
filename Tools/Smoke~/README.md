# Smoke sources for CI

The Tier-3 smoke test lives in the local dev harness project
(`HappyTextDev/Assets/Smoke/`), which is not part of this repository. CI
cannot see it, so the two source files the Windows smoke job needs are
carried here and copied into the throwaway CI project by the workflow:

- `SmokeSelfTest.cs` is a byte-for-byte copy of
  `HappyTextDev/Assets/Smoke/SmokeSelfTest.cs`. If you change one, change
  the other; the mobile smoke runs use the harness copy, the Windows CI
  job uses this one.
- `SmokeBuildCI.cs` is CI-only (Windows standalone, Mono). The harness
  project has its own `SmokeBuild.cs` for Android and the iOS simulator.

The trailing tilde on this directory keeps Unity from importing any of it
into the package. The fonts the self-test loads are assembled by the
workflow: two ship in `Tests/Fonts~/`, the other four are downloaded from
the same URLs `Tools/fetch_coverage_fonts.py` uses.
