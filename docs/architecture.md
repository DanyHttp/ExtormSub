# Architecture decision log

Full design: [../ARCHITECTURE.md](../ARCHITECTURE.md). Each entry records one decision: the context, the choice and its consequence.

### ADR-001 — Native .NET + whisper.cpp (Whisper.net) instead of a Python sidecar
- **Context:** The app must feel like a lightweight native utility. faster-whisper needs a Python runtime and CTranslate2.
- **Decision:** Use whisper.cpp in-process through Whisper.net, shipping the CPU and Vulkan runtimes.
- **Consequence:** A single process and a small install. Vulkan uses the GPU on AMD, Intel and NVIDIA. `IASRProvider` leaves room for a `FasterWhisperProvider` sidecar later.

### ADR-002 — Core logic lives in a UI-free `ExtormSub.Core`
- **Decision:** The pipeline, stabilizer, sequencer, translation queue and settings sit in a plain `net8.0` library. Engines depend on it, never the reverse.
- **Consequence:** Everything that decides *what* is shown and *when* can be unit-tested without WPF, audio hardware or models.

### ADR-003 — One OpenAI-compatible provider, many presets
- **Context:** OpenAI, DeepSeek, OpenRouter, Gemini and Anthropic all expose `/chat/completions`-compatible endpoints.
- **Decision:** `OpenAiCompatibleProvider` plus presets (base URL and default model). `ITranslationProvider` stays open for native SDK providers.

### ADR-004 — Stream time from sample count + synthetic silence
- **Context:** WASAPI loopback sends no packets while nothing plays, so an utterance open when playback stops would never close.
- **Decision:** The DSP worker waits on the ring with a timeout and injects zero frames equal to the wall-clock gap.
- **Consequence:** Utterances always close, and SRT/VTT timestamps follow wall time.

### ADR-005 — Translation ordering by per-utterance sequence number
- **Decision:** `SubtitleSequencer` releases results in seq order. A lower seq blocks higher ones until it closes or its head timeout expires.
- **Consequence:** Subtitles never appear out of order. A stuck request cannot freeze the display.

### ADR-006 — API keys in DPAPI, not Credential Manager
- **Decision:** DPAPI (CurrentUser) encrypted blobs under `%AppData%\ExtormSub\secrets`.
- **Why:** One call, no P/Invoke structs, bound to the Windows user account. Credential Manager would add UI visibility and nothing we need.

### ADR-007 — Overlay text rendered from geometry (`OutlinedText`)
- **Decision:** A custom `FrameworkElement` builds `FormattedText` → geometry, then draws the stroke and the fill.
- **Why:** WPF `TextBlock` cannot outline text. `FormattedText` also gives `MaxLineCount`, RTL and line height in one place.

### ADR-008 — Only CPU + Vulkan whisper runtimes are bundled
- **Why:** The CUDA runtime adds hundreds of MB and needs a matching CUDA install. Vulkan covers every GPU vendor. The crash sentinel forces CPU after a native GPU-init crash.

### ADR-009 — The English line is live; the translation line is the latest in-order translation
- **Context:** Translation always lags speech by one API round trip.
- **Decision:** The small English line follows the current utterance (partials, then final). The large line shows the most recent translation released by the sequencer.
- **Consequence:** Subtitles feel real-time. For about a second after a new sentence starts, the two lines can belong to adjacent sentences. `DisplayMode.TranslationOnly` hides this completely.

### ADR-010 — Sized JSON bodies, and no proxy for loopback endpoints
- **Context:** Found in the live test. `JsonContent` sends chunked bodies, which simple OpenAI-compatible servers reject. .NET also sent `127.0.0.1` through the user's system proxy, despite the bypass list.
- **Decision:** Requests use `StringContent`, which sends a `Content-Length`. Loopback base URLs (Ollama, LM Studio) use a separate `HttpClient` with `UseProxy = false`.

### ADR-011 — The DI container is not disposed at exit
- **Decision:** `App.ExitAsync` disposes capture, history, overlay, hotkeys and tray in order, on the UI thread, then stops the host without disposing the container.
- **Why:** Container disposal would dispose the same UI objects again, from a thread-pool thread, after an `await ConfigureAwait(false)`. The process is exiting anyway.

### ADR-012 — ASR engine is chosen per session, not per process
- **Decision:** `SubtitlePipeline.Start` takes the `IASRProvider`. `ListeningController` holds whisper.cpp and faster-whisper and unloads the one not in use.
- **Consequence:** Switching engines is a normal restart of listening. `DisposeAsync` on a provider unloads it, and the provider can be initialized again.

### ADR-013 — faster-whisper as a sidecar in a private venv
- **Decision:** Python stays out of the ExtormSub process. A stdin/stdout sidecar gets float32 audio and returns JSON lines; fd 1 is redirected to stderr inside the sidecar so native libraries cannot corrupt the protocol. The venv lives under `%LocalAppData%\ExtormSub\faster-whisper`.
- **Why:** A crash or OOM in CTranslate2 cannot take ExtormSub down; the provider restarts the sidecar. The user's Python installation is never modified.

### ADR-014 — NVIDIA support is an on-demand pack, not bundled
- **Context:** The whisper CUDA 12 runtime is 250 MB and cuBLAS 553 MB, compressed. Most users don't have NVIDIA GPUs, and Vulkan already works on them.
- **Decision:** The pack downloads on request, pinned by SHA-256, and is loaded via `RuntimeOptions.LibraryPath` + PATH. An installed CUDA 12 toolkit is reused.

### ADR-015 — Spool newest-first, drop oldest-on-disk
- **Decision:** An utterance arriving at an empty queue always stays in memory. Beyond the memory budget, *new* utterances go to disk; beyond the disk budget, the *oldest on-disk* ones are dropped. ASR always consumes in order, and late results go to history only.
- **Why:** Nothing touches the disk while ASR keeps up, the next utterance for ASR is always in memory, and on-screen subtitles never lag behind reality.

### ADR-016 — Downloads verify against pinned hashes
- **Decision:** Every downloaded artifact (models, CUDA pack, Inno Setup compiler) has a SHA-256 pinned in source. Downloads resume through `.part` files and HTTP Range.
