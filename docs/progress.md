# Progress

## Phase 0: Groundwork (done)

**Implemented:** I studied the six reference repositories; the notes are in ARCHITECTURE.md §1. I wrote ARCHITECTURE.md, IMPLEMENTATION_PLAN.md and the decision log, and set up four projects with pinned packages. The .NET 8 SDK 8.0.425 was installed per-user in `%LOCALAPPDATA%\Microsoft\dotnet`, because the machine had only the runtime.

**Licenses:** All dependencies are MIT or Apache-2.0. Aria is GPL-3.0, so it was only read, never copied.

## Phase 1: Core logic (done)

**Implemented:** `ExtormSub.Core` has no UI and no native dependencies. It contains:
- the ring buffer
- the PCM converter and WDL resampler
- the VAD segmenter (pre-roll, post-roll, minimum speech, silence timeout, max-length split)
- the energy VAD fallback
- the transcript stabilizer and filter
- the glossary
- the translation cache, context manager, prompt and queue (concurrency, rate limit, backoff, per-seq cancel, dedupe)
- the OpenAI-compatible provider and presets
- the subtitle sequencer
- the TXT/SRT/VTT exporters
- the settings schema and atomic store
- pipeline metrics
- `SubtitlePipeline`
- `HistoryRecorder`

**Tested:** 87 unit tests. Among them:
- a randomized ordering test: 50 rounds × 30 segments
- an end-to-end pipeline run with fake audio, VAD, ASR and translator, where translations finish out of order but display in order
- an utterance closing when loopback goes silent
- a translation failure falling back to English
- device loss and switching

## Phase 2: Windows engines (done)

**Implemented:**
- WASAPI loopback source and device service (follows the default device; falls back when a device is removed)
- Silero v5 VAD through ONNX, allocation-free per frame
- `WhisperCppProvider`: loaded once, warmed up, Vulkan → CPU runtime order, GPU crash sentinel, padding for clips under 1 s
- hardware detector
- model catalog and downloader (`.part` file with atomic rename, disk-space check)
- SQLite history
- DPAPI secret store

**Tested:** On real hardware, whisper.cpp transcribes TTS speech correctly, and Silero detects and segments real speech. Also covered: the SQLite round trip (search treats `%` literally, cascade delete), DPAPI (no plaintext on disk), WASAPI format mapping, and downloader progress and truncation cleanup. The suite is now 97 tests, all passing.

## Phase 3: Vertical slice UI (done)

**Implemented:**
- Tray app: single instance, the `--minimized` and `--exit` switches, global exception guards
- Dark tray menu and a tray icon that changes with state
- Overlay:
  - tool window, never activates, click-through when locked
  - edit mode with drag, resize and a Lock button
  - emergency hotkey
  - `OutlinedText` renderer: outline, RTL, auto-shrink, keeps the tail of live text
  - fade and subtitle duration
  - presets and custom position, remembered per monitor in physical pixels
- Configurable global hotkeys, with conflict reporting
- Settings window with 10 pages and debounced auto-save
- Model manager (download, cancel, delete, use; custom folder)
- Translation "Test connection"
- Glossary editor
- Live diagnostics
- History window: search, copy, delete, clear, export TXT/SRT/VTT

**Tested live on this PC:** Windows TTS was played through the headphone device.
- Loopback, Silero and whisper.cpp ran on Vulkan (RX 5700 XT).
- The glossary fixed "chat GPT and git hub" to "ChatGPT and GitHub".
- A local mock translation server received prompts carrying context and glossary hints.
- The overlay showed English partials and the Persian translation, with correct RTL and bidi.
- History rows were written with latencies.

Also tested through UI Automation and synthesized keystrokes:
- navigating the settings pages
- Test connection
- edit mode → move → lock, with the custom bounds and monitor persisted
- Ctrl+Alt+S, Ctrl+Alt+C and Ctrl+Alt+Shift+C
- single-instance handoff
- graceful `--exit`

Measurements are in ARCHITECTURE.md §12.

**Bugs found by the live test and fixed:**
1. Chunked request bodies broke simple OpenAI-compatible servers. A regression test now covers it.
2. Loopback URLs were routed through the system proxy.
3. The overlay band had too little headroom for two lines of translation plus English.

## Phase 5: Six follow-up features + real-provider test (done)

**Real translation test:** used your OpenRouter key and `inclusionai/ling-3.0-flash-fin:free`.
- Test connection: 2.6 s. A live TTS session translated correctly with context ("that trick was sick" → «اون ترفند عالی بود»).
- The free model sometimes returns HTTP 429 (rate-limited upstream). 429s now back off from 2 s instead of 0.5 s.
- Translation-only edits (model, provider, API key) swap the translation queue live instead of restarting capture and ASR.

| Feature | Implemented | Tested |
|---|---|---|
| **Installer** (Inno Setup) | `build/build-installer.ps1` runs the tests, a self-contained publish (`--artifacts-path` so dev builds are never touched), removes non-x64 runtimes, then compiles `installer/ExtormSub.iss` with a SHA-pinned Tools.InnoSetup compiler, so no admin rights are needed. Per-user install by default. Upgrades close the running app via `--exit`. Uninstall removes the Run key and asks about user data (silent uninstall keeps it). | Built `artifacts/ExtormSub-Setup-0.2.0.exe` (59 MB, self-contained). Silent install → launch → upgrade over the running app (closed automatically) → uninstall (files, shortcut, registry removed; data kept). |
| **Microphone input** | `WasapiSource` covers loopback and capture; `AudioDeviceService` lists both kinds and follows each kind's default device. Settings → Audio → "Listen to". A clear message when Windows privacy blocks the microphone. | Real mic opened (44.1 kHz stereo float) and delivered 101 % of real time. The pipeline is source-agnostic and already tested. |
| **Resumable + verified downloads** | `FileDownloader`: `.part` file, HTTP Range resume, SHA-256 of the whole file before rename, mismatch deletes. Exact sizes and SHA-256s for every catalog model are pinned from Hugging Face. UI shows "Paused at X of Y MB" and a Resume button. | Unit tests: interrupted download resumes with a Range request from the right offset; checksum mismatch deletes; your local base.en matches its pinned hash. |
| **Disk spooling when ASR lags** | `UtteranceSpool`: backlog in memory up to the limit, then 16-bit PCM files in `%LocalAppData%\ExtormSub\spool`, capped by a disk limit (default 30 min). Nothing is written while ASR keeps up, and leftovers are cleaned at start. Speech recognised later than "Late subtitles" (8 s) goes to history but not the screen. | Unit tests (FIFO, spill, drop policy, crash leftovers) and a pipeline test with a slow ASR: 5/5 segments recorded, 0 dropped, late ones marked stale, spool folder empty afterwards. A drop-policy bug was found by the test and fixed. |
| **NVIDIA CUDA** | Optional "NVIDIA acceleration pack", downloaded on demand (~800 MB, too big to bundle): Whisper.net CUDA 12 runtime from NuGet + NVIDIA cudart/cuBLAS wheels from PyPI (skipped if a CUDA 12 toolkit exists), all SHA-256 pinned and resumable. Runtime order CUDA12 → Vulkan → CPU; the crash sentinel still applies. | Download/extract/activation code is covered by the downloader tests. **CUDA inference itself is untested: this PC has an AMD GPU.** The UI correctly reports "No NVIDIA GPU detected". |
| **faster-whisper engine** | `FasterWhisperProvider` runs a Python sidecar (`Sidecar/faster_whisper_server.py`) over stdin/stdout, loads once, warms up, restarts itself if the sidecar dies, and exits when ExtormSub dies. `FasterWhisperEnvironment` creates a **private venv** (your own Python is untouched), with CUDA libraries on NVIDIA. Settings → Speech Recognition → Engine. | Setup via the app code: 60 s. base.en transcribed the TTS clip perfectly (0.96 confidence, ~650 ms on CPU). A live app session with small.en plus real OpenRouter translation worked: ~1.75 s per sentence on CPU int8. Kill-and-restart covered by a test. |

**Also fixed:** a progress bar bound two-way to a read-only property (the exception guard caught and logged it); the Audio page subtitle still said the app ignores microphones.

**Tests:** 113, all passing.

## Phase 6: Code signing + in-app updates (done)

**Code signing:** `build-installer.ps1` gained `-CertThumbprint` or `-CertFile`/`-CertPassword`, which can also be set through `EXTORMSUB_CERT_*` environment variables. It signs the following with SHA-256 and a DigiCert RFC 3161 timestamp:
- `ExtormSub.exe` and the three ExtormSub DLLs
- Setup and the uninstaller, through Inno's `SignTool`

signtool comes from the Windows SDK, or from the SHA-pinned `Microsoft.Windows.SDK.BuildTools` package. Without a certificate, the build warns and stays unsigned.

**Updates:** The build writes `latest.json` (version, relative installer URL, SHA-256, size). The app bakes in its URL from `UpdateManifestUrl` in the csproj; if that is empty, update checks are off.
- **Checks:** 30 s after start, then every 24 h. The setting General → Check for updates (default on) is toggled on the About page, where "Check now" also lives. A tray balloon appears once per version, and clicking it opens About.
- **Install:**
  - The download is resumable (`FileDownloader`) and its SHA-256 is verified.
  - If the running exe has a valid signature, the installer must be validly signed by the same subject (`WinVerifyTrust`). Otherwise it is deleted.
  - Setup then runs with `/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1`. It reuses the previous install mode, closes ExtormSub through `--exit`, and starts it again with `--minimized`.

**Tested:**
- 14 new unit tests:
  - manifest parsing (relative URL, HTTPS-only, hash format, garbage)
  - version comparison (0.3.0 == 0.3.0.0)
  - download plus checksum
  - a tampered installer is deleted
  - a signed build refuses an unsigned installer
  - Authenticode on a Microsoft-signed file versus an unsigned one
- A build with a temporary self-signed certificate signed the binaries, the uninstaller and Setup, with a DigiCert timestamp. The certificate was removed afterwards.
- **Not yet run:** the silent upgrade with `/RELAUNCH=1` from the app, which needs an HTTPS feed.

**Tests:** 127, all passing.

## Known limitations

- **CUDA** (whisper.cpp pack and faster-whisper CUDA) is untested on real NVIDIA hardware.
- **faster-whisper** needs Python 3.9+ on the PC to create its private environment. The first use of each model downloads it (small.en took about 2 minutes here); the status says "Starting faster-whisper…" but shows no progress percentage.
- **Your free OpenRouter model** is rate-limited upstream and translates idioms unevenly. A paid or larger model will be better and faster.
- **Stale subtitles.** Spooled speech is caught up for history, but subtitles recognised late are not shown, by design.
- **Model downloads** are verified at download time only. There is no "re-verify" button yet (`ModelDownloader.VerifyAsync` exists).
- Items from Phase 3 still apply: long speech is split at a hard boundary, English and Persian lines can briefly show adjacent sentences, and games in exclusive fullscreen can't show the overlay.
- **Installer:** English-only. Signing is supported, but you need a real certificate. Even when signed, SmartScreen can still warn until the certificate builds reputation (since 2024 that is true of EV certificates too).
- **PFX password:** Inno Setup prints the signing command, so a PFX password shows up in the build output. Prefer a certificate in the store (`-CertThumbprint`).

## Next recommended step

1. Try a stronger translation model on OpenRouter, e.g. `google/gemini-2.5-flash` or `deepseek/deepseek-chat`.
2. Buy or obtain a code-signing certificate, set `UpdateManifestUrl`, and publish the first release (installer + `latest.json`).
3. On an NVIDIA machine, test the CUDA pack and faster-whisper CUDA.
