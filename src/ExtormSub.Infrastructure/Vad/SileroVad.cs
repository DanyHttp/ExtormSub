using ExtormSub.Core.Audio;
using Microsoft.ML.OnnxRuntime;

namespace ExtormSub.Infrastructure.Vad;

/// <summary>
/// Silero VAD v5 via ONNX Runtime. 512-sample frames at 16 kHz, with the 64-sample context the
/// reference implementation prepends. All tensors are preallocated: Process() does not allocate.
/// </summary>
public sealed class SileroVad : IVoiceActivityDetector
{
    private const int Context = 64;
    private const int Frame = 512;

    private readonly InferenceSession _session;
    private readonly RunOptions _run = new();
    private readonly float[] _input = new float[Context + Frame];
    private readonly float[] _state = new float[2 * 1 * 128];
    private readonly float[] _stateOut = new float[2 * 1 * 128];
    private readonly float[] _prob = new float[1];
    private readonly long[] _sr = [AudioFormat.AsrSampleRate];
    private readonly OrtValue[] _inputs;
    private readonly OrtValue[] _outputs;
    private static readonly string[] InputNames = ["input", "state", "sr"];
    private static readonly string[] OutputNames = ["output", "stateN"];

    public SileroVad(string modelPath)
    {
        var options = new SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };
        _session = new InferenceSession(modelPath, options);
        _inputs =
        [
            OrtValue.CreateTensorValueFromMemory(_input, [1, Context + Frame]),
            OrtValue.CreateTensorValueFromMemory(_state, [2, 1, 128]),
            OrtValue.CreateTensorValueFromMemory(_sr, []),
        ];
        _outputs =
        [
            OrtValue.CreateTensorValueFromMemory(_prob, [1, 1]),
            OrtValue.CreateTensorValueFromMemory(_stateOut, [2, 1, 128]),
        ];
    }

    public string Name => "Silero v5";
    public int FrameSize => Frame;

    public float Process(ReadOnlySpan<float> frame)
    {
        frame.CopyTo(_input.AsSpan(Context));
        _session.Run(_run, InputNames, _inputs, OutputNames, _outputs);
        _stateOut.CopyTo(_state, 0);
        // Next call's context is the tail of this frame.
        _input.AsSpan(Frame, Context).CopyTo(_input);
        return _prob[0];
    }

    public void Reset()
    {
        Array.Clear(_state);
        Array.Clear(_input);
    }

    public void Dispose()
    {
        foreach (var v in _inputs) v.Dispose();
        foreach (var v in _outputs) v.Dispose();
        _run.Dispose();
        _session.Dispose();
    }
}
