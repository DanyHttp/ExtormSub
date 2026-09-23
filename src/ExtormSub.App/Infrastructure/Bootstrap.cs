using System.Net.Http;
using System.Net.Http.Headers;
using ExtormSub.App.Hotkeys;
using ExtormSub.App.Overlay;
using ExtormSub.App.Services;
using ExtormSub.App.Tray;
using ExtormSub.App.UI;
using ExtormSub.Core.Diagnostics;
using ExtormSub.Core.History;
using ExtormSub.Core.Settings;
using ExtormSub.Infrastructure;
using ExtormSub.Infrastructure.ASR;
using ExtormSub.Infrastructure.Audio;
using ExtormSub.Infrastructure.History;
using ExtormSub.Infrastructure.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace ExtormSub.App.Infrastructure;

/// <summary>Composition root: logging, DI registrations. Everything is a singleton with app lifetime.</summary>
public static class Bootstrap
{
    public static IHost Build()
    {
        var paths = new AppPaths();
        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning) // never log request headers
            .Enrich.FromLogContext()
            .WriteTo.File(Path.Combine(paths.LogsDirectory, "extormsub-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7, fileSizeLimitBytes: 20 << 20,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog(dispose: true);

        var s = builder.Services;
        s.AddSingleton(paths);
        s.AddSingleton(levelSwitch);
        s.AddSingleton(sp => new SettingsStore(paths.SettingsFile, sp.GetRequiredService<ILogger<SettingsStore>>()));
        s.AddSingleton<ISecretStore>(_ => new DpapiSecretStore(paths.SecretsDirectory));
        s.AddSingleton<PipelineMetrics>();
        s.AddSingleton<CudaRuntimePack>();
        s.AddSingleton(sp => new WhisperCppProvider(paths, sp.GetRequiredService<CudaRuntimePack>(), sp.GetRequiredService<ILogger<WhisperCppProvider>>()));
        s.AddSingleton<FasterWhisperEnvironment>();
        s.AddSingleton<FasterWhisperProvider>();
        s.AddSingleton<AudioDeviceService>();
        s.AddSingleton<IHistoryStore>(_ => new SqliteHistoryStore(paths.HistoryDatabase));
        s.AddSingleton<HistoryRecorder>();
        s.AddSingleton(_ =>
        {
            // Per-request timeouts are enforced by the translation provider.
            var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ExtormSub", "0.1"));
            return http;
        });
        s.AddSingleton<ListeningController>();
        s.AddSingleton<ModelLibrary>();
        s.AddSingleton<UpdateController>();
        s.AddSingleton<OverlayController>();
        s.AddSingleton<HotkeyService>();
        s.AddSingleton<TrayIcon>();
        s.AddSingleton<UiShell>();
        s.AddTransient<HistoryViewModel>();
        return builder.Build();
    }

    public static LogEventLevel ParseLevel(string? level) =>
        Enum.TryParse<LogEventLevel>(level, true, out var l) ? l : LogEventLevel.Information;
}
