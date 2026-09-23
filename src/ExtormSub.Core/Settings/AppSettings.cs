using ExtormSub.Core.ASR;
using ExtormSub.Core.Text;

namespace ExtormSub.Core.Settings;

/// <summary>
/// The persisted, typed settings schema. Every property has a safe default, so missing or partial
/// JSON always loads. Secrets (API keys) are NOT stored here — see <see cref="ISecretStore"/>.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public GeneralSettings General { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public VadSettings Vad { get; set; } = new();
    public AsrSettings Asr { get; set; } = new();
    public TranslationSettings Translation { get; set; } = new();
    public List<GlossaryEntry> Glossary { get; set; } = Text.Glossary.Defaults();
    public OverlaySettings Overlay { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public HistorySettings History { get; set; } = new();
    public AdvancedSettings Advanced { get; set; } = new();

    public AppSettings Clone() => SettingsStore.Deserialize(SettingsStore.Serialize(this));

    /// <summary>Clamps out-of-range values (hand-edited JSON) instead of failing.</summary>
    public AppSettings Normalize()
    {
        General ??= new(); Audio ??= new(); Vad ??= new(); Asr ??= new(); Translation ??= new();
        Glossary ??= []; Overlay ??= new(); Hotkeys ??= new(); History ??= new(); Advanced ??= new();

        Audio.RingBufferSeconds = Math.Clamp(Audio.RingBufferSeconds, 2, 60);
        Vad.SpeechThreshold = Math.Clamp(Vad.SpeechThreshold, 0.05f, 0.95f);
        Vad.MinSpeechMs = Math.Clamp(Vad.MinSpeechMs, 32, 2000);
        Vad.SilenceTimeoutMs = Math.Clamp(Vad.SilenceTimeoutMs, 100, 3000);
        Vad.PreRollMs = Math.Clamp(Vad.PreRollMs, 0, 1000);
        Vad.PostRollMs = Math.Clamp(Vad.PostRollMs, 0, 1000);
        Vad.MaxUtteranceSeconds = Math.Clamp(Vad.MaxUtteranceSeconds, 3, 28);
        Asr.PartialIntervalMs = Math.Clamp(Asr.PartialIntervalMs, 250, 5000);
        Asr.StabilityDebounceMs = Math.Clamp(Asr.StabilityDebounceMs, 100, 3000);
        Asr.Threads = Math.Clamp(Asr.Threads, 0, 32);
        Translation.Temperature = Math.Clamp(Translation.Temperature, 0, 2);
        Translation.TimeoutSeconds = Math.Clamp(Translation.TimeoutSeconds, 2, 120);
        Translation.MaxRetries = Math.Clamp(Translation.MaxRetries, 0, 10);
        Translation.MaxConcurrentRequests = Math.Clamp(Translation.MaxConcurrentRequests, 1, 8);
        Translation.MaxRequestsPerMinute = Math.Clamp(Translation.MaxRequestsPerMinute, 0, 1000);
        Translation.ContextSize = Math.Clamp(Translation.ContextSize, 0, 10);
        Overlay.TranslationFontSize = Math.Clamp(Overlay.TranslationFontSize, 10, 120);
        Overlay.OriginalFontSize = Math.Clamp(Overlay.OriginalFontSize, 8, 100);
        Overlay.BackgroundOpacity = Math.Clamp(Overlay.BackgroundOpacity, 0, 1);
        Overlay.TextOpacity = Math.Clamp(Overlay.TextOpacity, 0.1, 1);
        Overlay.OutlineThickness = Math.Clamp(Overlay.OutlineThickness, 0, 10);
        Overlay.MaxWidth = Math.Clamp(Overlay.MaxWidth, 300, 4000);
        Overlay.MaxLines = Math.Clamp(Overlay.MaxLines, 1, 5);
        Overlay.BottomMargin = Math.Clamp(Overlay.BottomMargin, 0, 1000);
        Overlay.LineSpacing = Math.Clamp(Overlay.LineSpacing, 0.8, 2.5);
        Overlay.SubtitleDurationSeconds = Math.Clamp(Overlay.SubtitleDurationSeconds, 1, 60);
        Overlay.FadeDurationMs = Math.Clamp(Overlay.FadeDurationMs, 0, 3000);
        Advanced.MaxAsrBacklogSeconds = Math.Clamp(Advanced.MaxAsrBacklogSeconds, 5, 300);
        Advanced.SequencerHeadTimeoutMs = Math.Clamp(Advanced.SequencerHeadTimeoutMs, 500, 30000);
        Advanced.TranslationCacheSize = Math.Clamp(Advanced.TranslationCacheSize, 10, 100_000);
        Advanced.MaxSpoolMinutes = Math.Clamp(Advanced.MaxSpoolMinutes, 1, 600);
        Advanced.StaleAfterSeconds = Math.Clamp(Advanced.StaleAfterSeconds, 2, 120);
        if (Asr.Engine != AsrEngines.FasterWhisper) Asr.Engine = AsrEngines.WhisperCpp;
        if (string.IsNullOrWhiteSpace(Hotkeys.EmergencyUnlock)) Hotkeys.EmergencyUnlock = new HotkeySettings().EmergencyUnlock;
        return this;
    }
}

public sealed class GeneralSettings
{
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; } = true;
    public bool StartListeningOnLaunch { get; set; }
    public bool CheckForUpdates { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool RememberOverlayPosition { get; set; } = true;
    public bool RememberMonitor { get; set; } = true;
    public bool PauseWhenSilent { get; set; } = true;
    public bool FirstRunCompleted { get; set; }
}

public enum AudioSourceKind { SystemAudio, Microphone }

public sealed class AudioSettings
{
    public AudioSourceKind Source { get; set; } = AudioSourceKind.SystemAudio;
    /// <summary>Playback endpoint id; null follows the Windows default device.</summary>
    public string? DeviceId { get; set; }
    /// <summary>Recording endpoint id; null follows the Windows default microphone.</summary>
    public string? MicrophoneId { get; set; }
    public int RingBufferSeconds { get; set; } = 10;
}

public sealed class VadSettings
{
    public bool UseSilero { get; set; } = true;
    public float SpeechThreshold { get; set; } = 0.5f;
    public int MinSpeechMs { get; set; } = 250;
    public int SilenceTimeoutMs { get; set; } = 500;
    public int PreRollMs { get; set; } = 300;
    public int PostRollMs { get; set; } = 150;
    public int MaxUtteranceSeconds { get; set; } = 10;
}

public static class AsrEngines
{
    public const string WhisperCpp = "whisper.cpp";
    public const string FasterWhisper = "faster-whisper";
}

public sealed class AsrSettings
{
    public string Engine { get; set; } = AsrEngines.WhisperCpp;
    /// <summary>Python used to create the faster-whisper environment; null = auto-detect.</summary>
    public string? PythonPath { get; set; }
    public AsrPreset Preset { get; set; } = AsrPreset.Balanced;
    /// <summary>Used when <see cref="Preset"/> is Custom.</summary>
    public string Model { get; set; } = "base.en";
    public AsrBackend Backend { get; set; } = AsrBackend.Auto;
    public string Language { get; set; } = "en";
    public int Threads { get; set; }
    public bool ShowPartials { get; set; } = true;
    public int PartialIntervalMs { get; set; } = 800;
    public int StabilityDebounceMs { get; set; } = 400;
    public bool TrimAudioContext { get; set; }
    /// <summary>Null = %LocalAppData%\ExtormSub\models.</summary>
    public string? ModelDirectory { get; set; }
}

public sealed class TranslationSettings
{
    public bool Enabled { get; set; } = true;
    public string Provider { get; set; } = "OpenAI";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "gpt-4.1-mini";
    public double Temperature { get; set; } = 0.3;
    public int TimeoutSeconds { get; set; } = 10;
    public int MaxRetries { get; set; } = 2;
    public int MaxConcurrentRequests { get; set; } = 2;
    public int MaxRequestsPerMinute { get; set; } = 60;
    public int ContextSize { get; set; } = 4;
    public string SourceLanguage { get; set; } = "English";
    public string TargetLanguage { get; set; } = "Persian";
    public bool ShowOriginalWhenUnavailable { get; set; } = true;
}

public enum DisplayMode { TranslationOnly, OriginalOnly, OriginalAndTranslation }

public enum OverlayPosition { BottomCenter, BottomLeft, BottomRight, TopCenter, Custom }

public enum TextWeight { Normal, Medium, SemiBold, Bold }

/// <summary>Screen rectangle in physical pixels.</summary>
public sealed record PixelRect(int X, int Y, int Width, int Height);

public sealed class OverlaySettings
{
    public DisplayMode DisplayMode { get; set; } = DisplayMode.OriginalAndTranslation;
    public string TranslationFont { get; set; } = "Segoe UI";
    public string OriginalFont { get; set; } = "Segoe UI";
    public TextWeight FontWeight { get; set; } = TextWeight.SemiBold;
    public double TranslationFontSize { get; set; } = 34;
    public double OriginalFontSize { get; set; } = 19;
    public string TranslationColor { get; set; } = "#FFFFFF";
    public string OriginalColor { get; set; } = "#D9D9D9";
    public string BackgroundColor { get; set; } = "#000000";
    public double BackgroundOpacity { get; set; } = 0.35;
    public double TextOpacity { get; set; } = 1.0;
    public bool Shadow { get; set; } = true;
    public bool Outline { get; set; } = true;
    public string OutlineColor { get; set; } = "#000000";
    public double OutlineThickness { get; set; } = 2.5;
    public double MaxWidth { get; set; } = 1100;
    public int MaxLines { get; set; } = 2;
    public double BottomMargin { get; set; } = 80;
    public double LineSpacing { get; set; } = 1.25;
    public double SubtitleDurationSeconds { get; set; } = 6;
    public int FadeDurationMs { get; set; } = 250;
    public OverlayPosition Position { get; set; } = OverlayPosition.BottomCenter;
    public PixelRect? CustomBounds { get; set; }
    /// <summary>Screen device name, e.g. \\.\DISPLAY2. Null = primary.</summary>
    public string? Monitor { get; set; }
    public bool Visible { get; set; } = true;
}

public sealed class HotkeySettings
{
    public string ToggleListening { get; set; } = "Ctrl+Alt+S";
    public string ToggleLock { get; set; } = "Ctrl+Alt+C";
    public string ToggleVisibility { get; set; } = "Ctrl+Alt+H";
    /// <summary>Always unlocks, shows and re-centers the overlay. Cannot be cleared.</summary>
    public string EmergencyUnlock { get; set; } = "Ctrl+Alt+Shift+C";
}

public sealed class HistorySettings
{
    public bool Enabled { get; set; } = true;
}

public sealed class AdvancedSettings
{
    public string LogLevel { get; set; } = "Information";
    public int MaxAsrBacklogSeconds { get; set; } = 30;
    public int SequencerHeadTimeoutMs { get; set; } = 4000;
    public int TranslationCacheSize { get; set; } = 2000;
    public float NoSpeechThreshold { get; set; } = 0.6f;
    /// <summary>When recognition falls behind, queue speech on disk instead of skipping it.</summary>
    public bool SpoolToDisk { get; set; } = true;
    public int MaxSpoolMinutes { get; set; } = 30;
    /// <summary>Speech recognised later than this is saved to history but not shown.</summary>
    public int StaleAfterSeconds { get; set; } = 8;
}
