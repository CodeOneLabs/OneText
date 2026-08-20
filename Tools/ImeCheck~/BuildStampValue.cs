// The commit the player was built from, written in by
// Tools/run_ime_check_win.sh. A const rather than something initialised at
// startup: RuntimeInitializeOnLoadMethod defaults to running after the scene
// loads, which is after the Awake that logs it, and the first run of this
// harness stamped every line "unknown".
public static class BuildStamp
{
    public const string Value = "COMMIT_PLACEHOLDER";
}
