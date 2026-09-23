"""
ExtormSub faster-whisper sidecar.

Loads one faster-whisper (CTranslate2) model and transcribes utterances sent by ExtormSub over stdin.

Protocol (little-endian):
  request  : int32 N (sample count; <= 0 means quit) followed by N float32 samples, 16 kHz mono
  response : one UTF-8 JSON line on stdout
             {"text", "language", "avg_logprob", "no_speech_prob", "ms"}  or  {"error": "..."}
  startup  : {"ready": true, "device": ..., "compute_type": ...}  or  {"error": "..."} then exit 1

stdout carries only protocol lines; everything else (library logs, warnings) goes to stderr.
When ExtormSub exits or crashes, stdin closes and this process exits too.
"""
import argparse
import json
import os
import struct
import sys
import time


def add_nvidia_dll_dirs():
    """pip-installed CUDA libraries (nvidia-cublas-cu12, nvidia-cudnn-cu12) live in site-packages/nvidia/*/bin,
    which Windows does not search. Register them before ctranslate2 loads."""
    if os.name != "nt":
        return
    import site
    for root in site.getsitepackages():
        nvidia = os.path.join(root, "nvidia")
        if not os.path.isdir(nvidia):
            continue
        for package in os.listdir(nvidia):
            bin_dir = os.path.join(nvidia, package, "bin")
            if os.path.isdir(bin_dir):
                os.add_dll_directory(bin_dir)
                os.environ["PATH"] = bin_dir + os.pathsep + os.environ.get("PATH", "")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", required=True)
    ap.add_argument("--device", default="cpu")
    ap.add_argument("--compute-type", default="int8")
    ap.add_argument("--language", default="en")
    ap.add_argument("--threads", type=int, default=0)
    ap.add_argument("--download-root", default=None)
    ap.add_argument("--prompt", default=None)
    ap.add_argument("--beam-size", type=int, default=1)
    args = ap.parse_args()

    # Keep a private handle on the real stdout for protocol lines; route everything else to stderr.
    # fd 1 itself is pointed at stderr so native libraries cannot corrupt the protocol either.
    proto = os.fdopen(os.dup(1), "w", encoding="utf-8", newline="\n")
    os.dup2(2, 1)
    sys.stdout = sys.stderr

    def emit(obj):
        proto.write(json.dumps(obj, ensure_ascii=False) + "\n")
        proto.flush()

    try:
        add_nvidia_dll_dirs()
        import numpy as np
        from faster_whisper import WhisperModel

        model = WhisperModel(
            args.model,
            device=args.device,
            compute_type=args.compute_type,
            cpu_threads=args.threads,
            download_root=args.download_root,
        )
        language = None if args.language in ("", "auto") else args.language
        options = dict(
            language=language,
            beam_size=args.beam_size,
            vad_filter=False,  # ExtormSub already segments speech with Silero
            condition_on_previous_text=False,
            without_timestamps=True,
            initial_prompt=args.prompt or None,
        )
        # Warm-up: first inference allocates buffers / kernels.
        list(model.transcribe(np.zeros(16000, dtype=np.float32), **options)[0])
    except Exception as e:  # noqa: BLE001 - reported to ExtormSub, which falls back to CPU / whisper.cpp
        emit({"error": f"{type(e).__name__}: {e}"})
        return 1

    emit({"ready": True, "device": args.device, "compute_type": args.compute_type})

    stdin = sys.stdin.buffer
    while True:
        header = read_exact(stdin, 4)
        if header is None:
            return 0
        (count,) = struct.unpack("<i", header)
        if count <= 0:
            return 0
        payload = read_exact(stdin, count * 4)
        if payload is None:
            return 0
        audio = np.frombuffer(payload, dtype="<f4")
        started = time.perf_counter()
        try:
            segments, info = model.transcribe(audio, **options)
            segments = list(segments)
            emit({
                "text": "".join(s.text for s in segments).strip(),
                "language": info.language,
                "avg_logprob": (sum(s.avg_logprob for s in segments) / len(segments)) if segments else None,
                "no_speech_prob": max((s.no_speech_prob for s in segments), default=1.0),
                "ms": (time.perf_counter() - started) * 1000,
            })
        except Exception as e:  # noqa: BLE001
            emit({"error": f"{type(e).__name__}: {e}"})


def read_exact(stream, n):
    buf = bytearray()
    while len(buf) < n:
        chunk = stream.read(n - len(buf))
        if not chunk:
            return None
        buf.extend(chunk)
    return bytes(buf)


if __name__ == "__main__":
    sys.exit(main())
