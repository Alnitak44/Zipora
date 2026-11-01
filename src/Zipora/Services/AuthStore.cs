using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Zipora.Services;

public class AuthStore
{
    public string LastExtractDir { get; set; } = "";
    public bool ArchiveBombProtectionEnabled { get; set; } = true;
    public string WindowBackdrop { get; set; } = "Mica";
    public string Theme { get; set; } = "Light";
    public string CustomBackgroundImagePath { get; set; } = "";

    private static string ConfigPath => AppStorage.DataFile("config.json");
    private bool _recoveredBackup;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AuthStore Load()
    {
        var store = new AuthStore();
        foreach (string path in new[] { ConfigPath, ConfigPath + ".bak" })
        {
          try
          {
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                var root = doc.RootElement;
                if (root.TryGetProperty("lastExtractDir", out var dir)) store.LastExtractDir = AppStorage.Layout.DecodePath(dir.GetString() ?? "");
                if (root.TryGetProperty("archiveBombProtection", out var protection) && protection.ValueKind == JsonValueKind.False) store.ArchiveBombProtectionEnabled = false;
                if (root.TryGetProperty("windowBackdrop", out var backdrop)) store.WindowBackdrop = backdrop.GetString() ?? "Mica";
                if (root.TryGetProperty("theme", out var theme)) store.Theme = theme.GetString() ?? "Light";
                if (root.TryGetProperty("customBackgroundImage", out var background)) store.CustomBackgroundImagePath = AppStorage.Layout.DecodePath(background.GetString() ?? "");
                store._recoveredBackup = path != ConfigPath;
                break;
            }
          }
          catch { }
        }
        return store;
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(ConfigPath);
        if (directory != null) Directory.CreateDirectory(directory);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            lastExtractDir = AppStorage.Layout.EncodePath(LastExtractDir),
            archiveBombProtection = ArchiveBombProtectionEnabled,
            windowBackdrop = WindowBackdrop,
            theme = Theme,
            customBackgroundImage = AppStorage.Layout.EncodePath(CustomBackgroundImagePath),
        }, JsonOptions);
        if (_recoveredBackup && File.Exists(ConfigPath)) File.Move(ConfigPath, ConfigPath + ".corrupt", true);
        AppStorage.WriteAtomic(ConfigPath, payload);
        _recoveredBackup = false;
    }
}
