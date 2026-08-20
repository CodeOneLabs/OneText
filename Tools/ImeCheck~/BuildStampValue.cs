// Overwritten by the build script with the commit the player was built from.
public static class BuildStampInit
{
    [UnityEngine.RuntimeInitializeOnLoadMethod]
    private static void Set() { BuildStamp.Value = "COMMIT_PLACEHOLDER"; }
}
