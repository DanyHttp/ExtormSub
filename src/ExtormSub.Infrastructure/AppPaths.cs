namespace ExtormSub.Infrastructure;

/// <summary>
/// Settings and secrets roam (%AppData%). Models, logs and the database stay on this machine (%LocalAppData%).
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string? roamingRoot = null, string? localRoot = null)
    {
        Roaming = roamingRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ExtormSub");
        Local = localRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ExtormSub");
        Directory.CreateDirectory(Roaming);
        Directory.CreateDirectory(Local);
    }

    public string Roaming { get; }
    public string Local { get; }
    public string SettingsFile => Path.Combine(Roaming, "settings.json");
    public string SecretsDirectory => Path.Combine(Roaming, "secrets");
    public string DefaultModelsDirectory => Path.Combine(Local, "models");
    public string LogsDirectory => Path.Combine(Local, "logs");
    public string HistoryDatabase => Path.Combine(Local, "history.db");
    public string GpuInitSentinel => Path.Combine(Local, "gpu-init.sentinel");
    public string UpdatesDirectory => Path.Combine(Local, "updates");

    /// <summary>Bundled next to the executable.</summary>
    public static string SileroModel => Path.Combine(AppContext.BaseDirectory, "assets", "silero_vad.onnx");

    public string ModelsDirectory(string? custom) =>
        string.IsNullOrWhiteSpace(custom) ? DefaultModelsDirectory : custom;
}
