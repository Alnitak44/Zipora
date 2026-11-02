namespace Zipora.Services;

/// <summary>Named handles let Setup/Uninstall detect desktop instances without killing tasks.</summary>
internal static class InstallationLifetime
{
    private static Mutex? _session;
    private static Mutex? _global;
    public static void Register()
    {
        if (AppStorage.IsPortable) return;
        _session ??= new Mutex(false, "Zipora.Desktop.Running");
        try { _global ??= new Mutex(false, @"Global\Zipora.Desktop.Running"); }
        catch (UnauthorizedAccessException) { /* An instance in another session may own the global handle. */ }
    }
}
