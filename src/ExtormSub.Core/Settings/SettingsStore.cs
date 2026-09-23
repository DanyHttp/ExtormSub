using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExtormSub.Core.Settings;

public interface ISecretStore
{
    string? Get(string name);
    /// <summary>Null or empty deletes the secret.</summary>
    void Set(string name, string? value);
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON. Saves are atomic (temp file + replace).
/// A corrupt file is moved aside and defaults are used, so the app always starts.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly ILogger _log;
    private readonly object _gate = new();
    private AppSettings _current;

    public SettingsStore(string path, ILogger<SettingsStore>? log = null)
    {
        _path = path;
        _log = (ILogger?)log ?? NullLogger.Instance;
        _current = Load();
    }

    public string FilePath => _path;

    /// <summary>A snapshot. Mutating it has no effect; use <see cref="Update"/>.</summary>
    public AppSettings Current { get { lock (_gate) return _current.Clone(); } }

    /// <summary>Raised after a successful save with (old, new) snapshots. Handlers run on the caller's thread.</summary>
    public event Action<AppSettings, AppSettings>? Changed;

    public void Update(Action<AppSettings> mutate)
    {
        AppSettings old, updated;
        lock (_gate)
        {
            old = _current;
            updated = old.Clone();
            mutate(updated);
            updated.Normalize();
            Save(updated);
            _current = updated;
        }
        Changed?.Invoke(old.Clone(), updated.Clone());
    }

    public void Replace(AppSettings settings) => Update(s => Copy(settings, s));

    private AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings().Normalize();
            var settings = Deserialize(File.ReadAllText(_path));
            if (settings.SchemaVersion > AppSettings.CurrentSchemaVersion)
                _log.LogWarning("Settings file is from a newer version ({Version}); unknown fields are ignored", settings.SchemaVersion);
            settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
            return settings.Normalize();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            var backup = _path + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            try { File.Move(_path, backup, overwrite: true); } catch (IOException) { }
            _log.LogError(ex, "Settings file was unreadable; moved to {Backup} and using defaults", backup);
            return new AppSettings().Normalize();
        }
    }

    private void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, Serialize(settings));
        File.Move(temp, _path, overwrite: true);
    }

    public static string Serialize(AppSettings s) => JsonSerializer.Serialize(s, Json);

    public static AppSettings Deserialize(string json) =>
        JsonSerializer.Deserialize<AppSettings>(json, Json) ?? throw new JsonException("Settings JSON was null");

    private static void Copy(AppSettings from, AppSettings to)
    {
        var clone = from.Clone();
        foreach (var p in typeof(AppSettings).GetProperties().Where(p => p.CanWrite))
            p.SetValue(to, p.GetValue(clone));
    }
}
