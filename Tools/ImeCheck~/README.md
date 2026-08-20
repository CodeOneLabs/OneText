# Windows IME check

An IME bug cannot be reproduced without a person and an input method, and
this package is developed on macOS. What this builds is the smallest thing
that lets someone on Windows answer the question in one double-click: a
window with one `OneTextInputField`, a live readout of the value, the
composition and their code points, the two reported sequences written out
as steps, and every log line mirrored to `ime-check.log` beside the
executable so a run that still misbehaves comes back as evidence.

    Tools/run_ime_check_win.sh [output directory]

Windows Build Support must be installed in the editor named by the script.
Mono rather than IL2CPP, for the same reason the Windows smoke job uses it:
cross-building IL2CPP from macOS wants a toolchain that is not here, and
nothing about an IME depends on the scripting backend.

The three files are copied into a throwaway project by the script, because
they are not package content: `ImeCheckHud.cs` and `BuildStampValue.cs` go
under `Assets/`, `ImeCheckBuild.cs` under `Assets/Editor/`.
