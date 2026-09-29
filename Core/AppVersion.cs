namespace CodexUsageNotch;

internal static class AppVersion
{
    public static string Display(Version? version) => version is null ? "unknown"
        : version.Revision > 0 ? version.ToString(4) : version.ToString(3);
}
