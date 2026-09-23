# ExtormSub — Implementation Plan

Each phase ends with `dotnet build` and `dotnet test` passing, plus a report in `docs/progress.md`.

## Phase 0 — Groundwork ✅
- ARCHITECTURE.md, this plan, docs/architecture.md (decision log), docs/progress.md
- Solution: Core / Infrastructure / App / Tests; pinned package versions

## Phase 1 — Core logic (test-first) ✅
1. `AudioRingBuffer`, `PcmConverter` (float32/PCM16/24/32 → mono float), `StreamResampler` (WDL)
2. `IVoiceActivityDetector`, `EnergyVad`, `VadSegmenter` (threshold, min speech, silence timeout, pre-roll, post-roll, max length)
3. `TranscriptStabilizer`, `TranscriptFilter` (hallucination / annotation stripping)
4. `Glossary` (alias → canonical, word-boundary, case-insensitive; prompt + translation hints)
5. `TranslationCache` (LRU, normalized key), `TranslationContextManager`, `SubtitlePrompt`
6. `TranslationQueue` (concurrency, rate limit, per-seq cancellation, retry/backoff, dedupe)
7. `SubtitleSequencer` (ordering / abandonment)
8. `OpenAiCompatibleProvider` (tested against a stub `HttpMessageHandler`)
9. `AppSettings` + `SettingsStore` (defaults, corrupt-file recovery, atomic save)
10. `SubtitleExporter` (TXT / SRT / VTT), `PipelineMetrics`
11. `SubtitlePipeline` orchestrator + an end-to-end test with fake source/VAD/ASR/translator

## Phase 2 — Windows engines ✅
1. `WasapiLoopbackSource` + `AudioDeviceService` (default-device follow, removal fallback)
2. `SileroVad` (ONNX, v5 I/O: input[1,576] incl. 64-sample context, state[2,1,128], sr)
3. `WhisperCppProvider` (load once, warm-up, runtime order, crash sentinel)
4. `HardwareDetector`, `ModelCatalog`, `ModelDownloader` (progress, cancel, .part → atomic move)
5. `SqliteHistoryStore`, `DpapiSecretStore`
6. Integration test: transcribe a TTS-generated WAV when a model is present (skipped otherwise)

## Phase 3 — Vertical slice UI ✅
1. Host/DI bootstrap, Serilog, single instance, global exception guards
2. Tray icon + menu, runtime-drawn state icons
3. Overlay window (click-through, no-activate, tool window), `OutlinedText`, RTL, fade, positions, edit mode
4. Hotkeys (configurable + emergency)
5. Settings window: General, Audio, Speech Recognition (+ model manager), Translation, Subtitle Appearance, Hotkeys, History, Advanced (+ diagnostics), About
6. Live run: SAPI text-to-speech played through speakers → loopback → whisper → overlay; translation against a local mock OpenAI server

## Phase 4 — Completion features ✅ (built together with Phase 3)
- History window (search, copy, delete, clear, export TXT/SRT/VTT)
- Glossary editor, diagnostics live view, start with Windows, remember monitor

## Deferred (documented, not built)
- Disk spooling of the ASR backlog (`IUtteranceSpool`)
- Microphone input (the `IAudioSource` seam already exists)
- CUDA runtime package; faster-whisper sidecar provider; Windows Speech provider
- Model checksum verification, resumable downloads
- Installer (MSIX / Inno Setup)
