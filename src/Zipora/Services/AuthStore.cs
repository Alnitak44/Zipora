using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Zipora.Services;

/// <summary>Persists local application preferences and the device fingerprint
/// used for cloud-service quota accounting. No activation credential is stored.</summary>
public class AuthStore
{
    public string DeviceFp { get; set; } = "";
    public string LastExtractDir { get; set; } = "";
    public bool AutoContributePassword { get; set; } = !AppStorage.IsPortable;
    public bool CompressContributePassword { get; set; }
    public bool ArchiveBombProtectionEnabled { get; set; } = true;
    public bool ZipSlipProtectionEnabled { get; set; } = true;
    public string WindowBackdrop { get; set; } = "Mica";
    public string Theme { get; set; } = "Light";
    public string CustomBackgroundImagePath { get; set; } = "";

    public UpdateFrequency UpdateFrequency { get; set; } = UpdateFrequency.Daily;
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

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
                if (root.TryGetProperty("deviceFp", out var fp)) store.DeviceFp = fp.GetString() ?? "";
                if (root.TryGetProperty("lastExtractDir", out var dir)) store.LastExtractDir = AppStorage.Layout.DecodePath(dir.GetString() ?? "");
                if (root.TryGetProperty("autoContribute", out var autoContribute) && autoContribute.ValueKind == JsonValueKind.True) store.AutoContributePassword = true;
                if (root.TryGetProperty("autoContribute", out autoContribute) && autoContribute.ValueKind == JsonValueKind.False) store.AutoContributePassword = false;
                if (root.TryGetProperty("compressContribute", out var compress) && compress.ValueKind == JsonValueKind.True) store.CompressContributePassword = true;
                if (root.TryGetProperty("archiveBombProtection", out var protection) && protection.ValueKind == JsonValueKind.False) store.ArchiveBombProtectionEnabled = false;
                if (root.TryGetProperty("zipSlipProtection", out var zipSlip) && zipSlip.ValueKind == JsonValueKind.False) store.ZipSlipProtectionEnabled = false;
                if (root.TryGetProperty("windowBackdrop", out var backdrop)) store.WindowBackdrop = backdrop.GetString() ?? "Mica";
                if (root.TryGetProperty("theme", out var theme)) store.Theme = theme.GetString() ?? "Light";
                if (root.TryGetProperty("customBackgroundImage", out var background)) store.CustomBackgroundImagePath = AppStorage.Layout.DecodePath(background.GetString() ?? "");
                if (root.TryGetProperty("updateFrequency", out var frequency) && Enum.TryParse<UpdateFrequency>(frequency.GetString(), out var parsed) && Enum.IsDefined(parsed)) store.UpdateFrequency = parsed;
                if (root.TryGetProperty("lastUpdateCheckUtc", out var checkedAt) && checkedAt.ValueKind == JsonValueKind.String && checkedAt.TryGetDateTimeOffset(out var timestamp)) store.LastUpdateCheckUtc = timestamp;
                store._recoveredBackup = path != ConfigPath;
                break;
            }
          }
          catch { }
        }
        if (string.IsNullOrEmpty(store.DeviceFp))
        {
            store.DeviceFp = AppStorage.IsPortable
                ? Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()
                : DeviceFingerprint.Compute();
            if (AppStorage.IsPortable) store.Save();
        }
        return store;
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(ConfigPath);
        if (directory != null) Directory.CreateDirectory(directory);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            deviceFp = DeviceFp,
            lastExtractDir = AppStorage.Layout.EncodePath(LastExtractDir),
            autoContribute = AutoContributePassword,
            compressContribute = CompressContributePassword,
            archiveBombProtection = ArchiveBombProtectionEnabled,
            zipSlipProtection = ZipSlipProtectionEnabled,
            windowBackdrop = WindowBackdrop,
            theme = Theme,
            updateFrequency = UpdateFrequency.ToString(),
            lastUpdateCheckUtc = LastUpdateCheckUtc,
            customBackgroundImage = AppStorage.Layout.EncodePath(CustomBackgroundImagePath),
        }, JsonOptions);
        if (_recoveredBackup && File.Exists(ConfigPath)) File.Move(ConfigPath, ConfigPath + ".corrupt", true);
        AppStorage.WriteAtomic(ConfigPath, payload);
        _recoveredBackup = false;
    }
}
