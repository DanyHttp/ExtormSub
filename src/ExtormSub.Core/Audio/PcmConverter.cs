using System.Buffers.Binary;
using NAudio.Dsp;

namespace ExtormSub.Core.Audio;

/// <summary>Converts interleaved device PCM to mono float by averaging channels.</summary>
public sealed class PcmConverter
{
    private readonly AudioFormat _format;

    public PcmConverter(AudioFormat format) => _format = format;

    /// <summary>Returns the number of mono samples written. <paramref name="destination"/> must hold bytes/BlockAlign samples.</summary>
    public int ConvertToMono(ReadOnlySpan<byte> source, Span<float> destination)
    {
        int frames = source.Length / _format.BlockAlign;
        int channels = _format.Channels;
        int bps = _format.BytesPerSample;
        float scale = 1f / channels;

        for (int f = 0; f < frames; f++)
        {
            var frame = source.Slice(f * _format.BlockAlign, _format.BlockAlign);
            float sum = 0;
            for (int c = 0; c < channels; c++)
                sum += ReadSample(frame.Slice(c * bps, bps));
            destination[f] = sum * scale;
        }
        return frames;
    }

    private float ReadSample(ReadOnlySpan<byte> s) => _format.Encoding switch
    {
        SampleEncoding.Float32 => BinaryPrimitives.ReadSingleLittleEndian(s),
        SampleEncoding.Pcm16 => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
        SampleEncoding.Pcm24 => ((s[2] << 24) | (s[1] << 16) | (s[0] << 8)) / 2147483648f,
        SampleEncoding.Pcm32 => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
        _ => 0f,
    };
}

/// <summary>Streaming mono resampler (NAudio's WDL sinc resampler). State persists between calls.</summary>
public sealed class StreamResampler
{
    private readonly WdlResampler? _wdl;
    private readonly double _ratio;

    public StreamResampler(int inputRate, int outputRate)
    {
        _ratio = (double)outputRate / inputRate;
        if (inputRate == outputRate) return;
        _wdl = new WdlResampler();
        _wdl.SetMode(true, 2, false);
        _wdl.SetFilterParms();
        _wdl.SetFeedMode(true); // input-driven
        _wdl.SetRates(inputRate, outputRate);
    }

    /// <summary>Upper bound of output samples for <paramref name="inputCount"/> input samples.</summary>
    public int MaxOutput(int inputCount) => (int)Math.Ceiling(inputCount * _ratio) + 16;

    public int Process(ReadOnlySpan<float> input, float[] output)
    {
        if (_wdl is null)
        {
            input.CopyTo(output);
            return input.Length;
        }
        if (input.IsEmpty) return 0;
        int needed = _wdl.ResamplePrepare(input.Length, 1, out float[] inBuf, out int inOffset);
        input[..Math.Min(needed, input.Length)].CopyTo(inBuf.AsSpan(inOffset));
        return _wdl.ResampleOut(output, 0, input.Length, output.Length, 1);
    }
}
