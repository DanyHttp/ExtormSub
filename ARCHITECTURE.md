# ExtormSub — Architecture

ExtormSub is a Windows tray utility. It captures system audio (WASAPI loopback) and transcribes speech locally with whisper.cpp. It translates stable English segments to Persian through an OpenAI-compatible API and shows the result as a click-through subtitle overlay.

Stack: .NET 8, C#, WPF. All native engines are loaded in-process; the app runs no Python.

---

## 1. What we took from the reference projects

| Project | Idea adopted | Idea rejected |
|---|---|---|
| **Handy** (MIT, Rust/Tauri) | Silero VAD wrapped in a smoothing layer (onset frames, hangover, pre-roll). Models are downloaded on demand into app data. The app lives in the tray. Settings use one typed schema with defaults. | Tauri/web UI |
| **Aria** (GPL-3.0) | Transcription and translation are separate stages. The overlay can be moved. There are several ASR backends. | **No code copied**: GPL is incompatible with a permissive app. Only the ideas were used. |
| **realtime-captions-system-audio** | A partial/stable split: a partial caption is shown at once and the stable one replaces it. The overlay is click-through. Transcripts are persisted. | Python runtime |
| **LiveCaptioner** (WPF) | `WS_EX_TRANSPARENT \| WS_EX_LAYERED` for click-through. A global hotkey restores interaction. Providers sit behind an interface. Subtitles are bilingual. | Its resampling runs *inside the WASAPI callback* and allocates a `MemoryStream` per packet. We never do that. |
| **VoxScribe** | Audio backlog is decoupled from recognition, with a disk-spool design. The glossary is applied to both the ASR output and the translation. Hardware auto-tuning. | Disk spooling in v1 (see §6). |
| **universal-translator** (MIT) | Context-aware translation prompt. | — |

---

## 2. Solution layout

```
src/
  ExtormSub.Core/            net8.0, no UI, no native code — all business logic, fully unit-tested
    Audio/                 AudioRingBuffer, PcmConverter, StreamResampler, VadSegmenter, EnergyVad, IAudioSource, IVoiceActivityDetector
    ASR/                   IASRProvider, TranscriptStabilizer, TranscriptFilter, ModelCatalog (presets, backend choice)
    Translation/           ITranslationProvider, OpenAiCompatibleProvider, TranslationQueue, TranslationCache,
                           TranslationContextManager, SubtitlePrompt, ProviderPresets
    Subtitles/             SubtitleSequencer (ordering), SubtitleExporter (TXT/SRT/VTT)
    Glossary/, Text/       Glossary (ASR normalization + translation hints), TextNormalizer
    Settings/              AppSettings (typed schema), SettingsStore (atomic JSON), ISecretStore
    History/               IHistoryStore + records
    Diagnostics/           PipelineMetrics (lock-free counters)
    Pipeline/              SubtitlePipeline — the orchestrator, wired only to interfaces
  ExtormSub.Infrastructure/  net8.0-windows — concrete Windows implementations
    Audio/                 WasapiLoopbackSource, AudioDeviceService (endpoint notifications)
    ASR/                   WhisperCppProvider (Whisper.net), ModelDownloader, HardwareDetector
    Vad/                   SileroVad (ONNX Runtime)
    History/               SqliteHistoryStore
    Security/              DpapiSecretStore
  ExtormSub.App/             WPF shell
    Overlay/               OverlayWindow, OutlinedText control, OverlayController, Win32 styles
    Tray/                  TrayIcon (NotifyIcon + dark menu)
    Hotkeys/               HotkeyService (RegisterHotKey on a message-only HWND)
    UI/                    SettingsWindow + pages, HistoryWindow, dark theme
    Infrastructure/        Host/DI bootstrap, single-instance, startup registration, exception guards
tests/ExtormSub.Tests        xUnit — Core logic and the Infrastructure pieces that need no hardware
```

**Dependency rule:** App → Infrastructure → Core. Core never references WPF, NAudio.Wasapi, Whisper.net or ONNX. The pipeline can therefore be tested end to end with fake audio, a fake ASR and a fake translator.

---

## 3. Data flow

```
 WASAPI loopback thread                 DSP worker (1 thread)                       ASR worker (1 thread)
 ─────────────────────                  ─────────────────────                       ─────────────────────
 DataAvailable(byte[])                  ring.Read ─► PcmConverter (→ mono float)    ┌ final queue  (FIFO, never dropped*)
   └─► AudioRingBuffer.Write  ────────►   ─► StreamResampler (→ 16 kHz)            │ partial slot (latest-wins, droppable)
       (memcpy only, no alloc)            ─► 512-sample frames ─► IVoiceActivityDetector
                                          ─► VadSegmenter ── SpeechStarted(seq) ──► SubtitleSequencer.Open(seq)
                                                          ── PartialDue(seq, audio)─► partial slot
                                                          ── SpeechEnded(seq, audio)► final queue
                                                                                    IASRProvider.Transcribe
                                                                                      ─► Glossary.ApplyToAsr
                                                                                      ─► TranscriptFilter
                                                                                      ─► TranscriptStabilizer
            ┌────────────────────────────────────────────────────────────────────────┘
            │ English (partial/final) ─► OverlayController (Dispatcher)
            ▼ eligible (final | stable ≥ debounce)
 TranslationQueue  ── cache hit? ─► result
   (async, ≤N concurrent, rate-limited, per-seq cancellation, exp. backoff)
   ─► ITranslationProvider (OpenAI-compatible HTTP)
   ─► SubtitleSequencer (re-orders by seq, drops obsolete) ─► OverlayController + history writer
```
`*` If ASR falls further behind than `Advanced.MaxAsrBacklogSeconds`, the oldest queued utterances are dropped, counted and logged. A live subtitle for audio from a minute ago is useless. This is also where disk spooling would go (§6).

### Segment identity and ordering
- `VadSegmenter` assigns every utterance a monotonically increasing `long Seq` when speech starts.
- `SubtitleSequencer` delivers translations to the UI strictly in `Seq` order:
  - A result for `seq` is displayed only when every lower seq is *closed*. A seq closes when its final translation is delivered, fails, turns out to be empty, or is abandoned.
  - If a higher seq has a finished final result and the head seq has been open longer than `HeadTimeout`, the head is **abandoned**. Its request is cancelled, and it falls back to English in history.
  - A result for a seq lower than the one already on screen is discarded.
- Within one seq, a newer text revision cancels the in-flight request for the older revision. The same text, even when promoted from stable to final, re-uses the in-flight request or the cache.

### Timestamps
Stream time is derived from the number of 16 kHz samples processed. When loopback delivers nothing (Windows sends no packets while nothing is playing), the DSP worker injects synthetic silence equal to the wall-clock gap. The stream clock therefore tracks real time, silence still closes open utterances, and SRT/VTT timestamps line up with the session.

---

## 4. Interfaces

```csharp
public interface IAudioSource : IDisposable            // Core/Audio
{
    AudioFormat Format { get; }
    string DeviceId { get; }
    string DeviceName { get; }
    event AudioDataHandler? DataAvailable;             // (ReadOnlySpan<byte>) — called on capture thread
    event EventHandler<Exception?>? Stopped;           // non-null exception = device lost/failed
    void Start(); void Stop();
}

public interface IVoiceActivityDetector : IDisposable   // Core/Audio
{
    int FrameSize { get; }                              // 512 @ 16 kHz for Silero v5
    float Process(ReadOnlySpan<float> frame);           // speech probability 0..1
    void Reset();
}

public interface IASRProvider : IAsyncDisposable        // Core/ASR
{
    string Name { get; }
    string BackendDescription { get; }                  // e.g. "whisper.cpp · Vulkan"
    bool IsReady { get; }
    AsrOptions? Current { get; }                        // re-initialize is a no-op when options are unchanged
    Task InitializeAsync(AsrOptions options, CancellationToken ct);   // load once + warm-up
    Task<AsrResult> TranscribeAsync(float[] samples16kMono, int count, CancellationToken ct);
}

public interface ITranslationProvider                   // Core/Translation
{
    string Name { get; }
    string CacheScope { get; }                          // provider+model, so a model switch never reuses stale cache
    Task<string> TranslateAsync(TranslationRequest request, CancellationToken ct);
    // throws TranslationException { Kind = Auth | RateLimited | Transient | Fatal }
}

public interface IHistoryStore  { /* sessions, segments, search, delete, clear */ }
public interface ISecretStore   { string? Get(string name); void Set(string name, string? value); }
```

Adding an engine means implementing the interface and registering it in `App/Infrastructure/Bootstrap.cs`. `FasterWhisperProvider` (a sidecar process) and `WindowsSpeechProvider` would plug in there. `OpenAiCompatibleProvider` already covers OpenAI, DeepSeek, OpenRouter, Gemini (OpenAI-compatible endpoint), Anthropic Claude (OpenAI-compatible endpoint) and any custom server. Provider *presets* only pre-fill the base URL and model.

---

## 5. Threading boundaries

| Thread | Owner | Allowed work | Must never |
|---|---|---|---|
| WASAPI capture | NAudio | `AudioRingBuffer.Write` (lock + memcpy) | allocate, convert, resample, log per packet, block |
| DSP worker | `SubtitlePipeline` (dedicated `Thread`, AboveNormal priority) | convert, resample, VAD (~0.1 ms/frame), segmentation, enqueue jobs | call ASR or network |
| ASR worker | `SubtitlePipeline` (dedicated `Thread`) | whisper inference, stabilizer, submit translation | touch WPF, wait on translation |
| Translation | thread pool (`async`) | HTTP, retry/backoff, cache | block the ASR or DSP threads |
| Timer tick (250 ms) | `SubtitlePipeline` | sequencer head timeout, queue metrics | heavy work |
| History writer | `Channel<T>` consumer | SQLite inserts | — |
| UI (Dispatcher) | WPF | render overlay, settings | receive work except via `Dispatcher.BeginInvoke` |

`SubtitlePipeline` raises plain .NET events from worker threads. `OverlayController` marshals each one with `Dispatcher.BeginInvoke`. Events arrive a few times per second at most (partials every ≥ 250 ms), so they are not coalesced. Every background loop takes a `CancellationToken`. `StopAsync` cancels them, joins the workers, disposes the capture and flushes the history writer.

---

## 6. Buffering

1. **AudioRingBuffer.** A bounded byte ring (default 10 s of the device format) between the capture thread and the DSP worker. It is preallocated. When full it overwrites the oldest audio and counts `DroppedBytes`. In practice it never fills, because DSP runs roughly 100× faster than real time.
2. **Utterance audio.** A `float[]` per utterance, grown geometrically. Partial snapshots are copies taken off the capture thread.
3. **ASR final queue.** An unbounded `Channel<Utterance>`, with a backlog limit measured in audio seconds.

**Disk spooling (implemented, `UtteranceSpool`).** Beyond the in-memory backlog, new utterances are written as 16 kHz 16-bit PCM to `%LocalAppData%\ExtormSub\spool` (the VoxScribe idea), up to a disk limit. The DSP worker only calls `Enqueue`. Utterances recognised later than `StaleAfter` are recorded in history but not shown on screen (ADR-015).

---

## 7. Latency-critical code

| Path | Budget | Notes |
|---|---|---|
| Capture callback | < 50 µs | memcpy into the ring under a short lock |
| DSP per 32 ms frame | < 1 ms | Silero ONNX with 1 intra-op thread; resampler state reused |
| End-of-speech detection | `SilenceTimeout` (default 500 ms) | the biggest configurable latency term |
| ASR final | 100–800 ms | model- and backend-dependent. The model stays loaded, is warmed up at start, and uses `WithNoContext` + `WithSingleSegment`. The prompt carries glossary terms. |
| Stabilizer | `StabilityDebounce` (400 ms) | two consecutive identical partial hypotheses ≥ debounce apart are translated *before* VAD end-of-speech |
| Translation | 300–1500 ms | API-bound. Cache hits are 0 ms, and ≤ N requests run concurrently. |
| Overlay render | < 16 ms | a custom `OutlinedText` draws a cached `FormattedText` geometry |

Measured independently in `PipelineMetrics`: ring fill, ASR latency, translation latency, speech-end→English, speech-end→Persian (total), queue depths, drops. The Diagnostics page shows them live.

---

## 8. Overlay

- The WPF window uses `AllowsTransparency`, `Topmost`, `ShowInTaskbar=false` and `ShowActivated=false`.
- Extended styles: `WS_EX_TOOLWINDOW` (not in Alt+Tab) and `WS_EX_NOACTIVATE` (never takes focus). **Locked** adds `WS_EX_TRANSPARENT | WS_EX_LAYERED` (mouse passes through).
- **Edit mode** clears `WS_EX_TRANSPARENT`, shows a frame and handles, and supports dragging (`DragMove`) and resizing (`WindowChrome.ResizeBorderThickness`). On lock, the bounds are saved in physical pixels along with the monitor device name.
- Positioning uses physical pixels through `SetWindowPos` against `Screen.WorkingArea`. The window is moved first so WPF processes `WM_DPICHANGED`, then sized. This keeps mixed-DPI multi-monitor setups correct. The app manifest declares PerMonitorV2.
- The window is a fixed "subtitle band" whose text is anchored to the bottom (or top, for top presets). A transparent click-through band costs nothing, and anchoring keeps growing text from pushing the band around.
- `OutlinedText` renders with `FormattedText` → geometry, which gives outline, RTL `FlowDirection`, wrapping, `MaxLines` and line height. English live text keeps the **tail**: leading words are dropped when it would exceed MaxLines, so a partial never grows into a block.
- Hotkeys: Ctrl+Alt+S (listen), Ctrl+Alt+C (lock/unlock), Ctrl+Alt+H (show/hide). Ctrl+Alt+Shift+C is the **emergency** key. It always shows the overlay, unlocks it and moves it back on-screen, and it cannot be unbound.

---

## 9. Hardware & ASR backend selection

`HardwareDetector` lists GPUs through WMI (NVIDIA/AMD/Intel), probes `vulkan-1.dll` and `nvcuda.dll`, and counts cores.
- **Auto:** Vulkan if a Vulkan loader and a discrete GPU exist, otherwise CPU. Only the CPU and Vulkan runtimes ship with the app. The CUDA runtime is several hundred MB and can be added as a NuGet runtime later. Vulkan already runs on NVIDIA, AMD and Intel.
- Whisper.net's `RuntimeOptions.RuntimeLibraryOrder` is set once per process (a library constraint), for example `[Vulkan, Cpu]`. If the Vulkan runtime fails to load, Whisper.net falls to the next runtime by itself.
- **Crash sentinel.** A marker file is written before GPU model load and deleted after warm-up. If it exists at startup, the previous GPU init crashed the process natively (which cannot be caught). The app then forces CPU and tells the user.
- Presets: `Fast` / `Balanced` / `Accurate` map to model ids by GPU availability (`AsrPresets`).

---

## 10. External dependencies & licenses

| Package | Use | License |
|---|---|---|
| NAudio.Core / NAudio.Wasapi 2.2.1 | WASAPI loopback, device notifications, WDL resampler | MIT |
| Whisper.net 1.9.1 (+ Runtime, Runtime.Vulkan) | whisper.cpp bindings | MIT (whisper.cpp: MIT) |
| Microsoft.ML.OnnxRuntime 1.22 | Silero VAD inference | MIT |
| Silero VAD v5 `silero_vad.onnx` (bundled, 2 MB) | VAD model | MIT |
| Microsoft.Data.Sqlite 8 | history | MIT (SQLite: public domain) |
| System.Security.Cryptography.ProtectedData 8 | DPAPI for API keys | MIT |
| System.Management 8 | WMI GPU/CPU probing | MIT |
| Microsoft.Extensions.Hosting / DI / Logging 8 | composition, lifetime, logging | MIT |
| Serilog + Serilog.Extensions.Hosting + Sinks.File | structured rolling file logs | Apache-2.0 |
| Whisper GGML models (downloaded at runtime from `huggingface.co/ggerganov/whisper.cpp`) | ASR weights | MIT |
| xunit | tests | Apache-2.0 |

No GPL code is included. Aria was only studied.

---

## 11. Security

- API keys are encrypted with DPAPI (CurrentUser scope) in `%AppData%\ExtormSub\secrets\<provider>.bin`. They never appear in `settings.json`, are never logged, and HTTP headers are never logged.
- Provider error bodies are truncated to 300 characters and scrubbed of anything that looks like a bearer token before logging.
- Settings are written atomically (temp file, then replace). A corrupt file is moved aside and defaults are used.

---

## 12. Measured on the development machine

These figures are from an i3-12100 and an RX 5700 XT (Vulkan) running base.en, with Windows TTS played through the speakers. The Diagnostics page shows the same numbers live.

| Stage | Result |
|---|---|
| Model load + warm-up (first time / cached) | 4.1 s / 0.65 s |
| Final ASR per utterance (Vulkan, base.en) | 170–180 ms |
| End of speech → English on screen | ~190–240 ms (plus the 500 ms silence timeout) |
| Translation (local mock with 350 ms latency) | 350–410 ms |
| CPU while listening to silence / to speech | 0.2 % / 2.8 % of the machine |
| Graceful exit (`ExtormSub.exe --exit`) | ~0.5 s, history session closed |
