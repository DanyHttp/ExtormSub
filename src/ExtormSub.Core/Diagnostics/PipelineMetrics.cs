namespace ExtormSub.Core.Diagnostics;

/// <summary>
/// Live counters written by pipeline workers and read by the diagnostics view about once a second.
/// Each field has a single writer; readers tolerate slightly stale values.
/// </summary>
public sealed class PipelineMetrics
{
    public volatile string DeviceName = "—";
    public volatile string AudioFormat = "—";
    public volatile string VadName = "—";
    public volatile string AsrBackend = "—";
    public volatile string AsrModel = "—";
    public volatile string TranslationProvider = "—";
    public volatile string TranslationHealth = "—";

    public int RingCapacityBytes;
    public int RingFillBytes;
    public long DroppedAudioBytes;
    public int AsrQueueDepth;
    public double AsrBacklogSeconds;
    public double SpooledSeconds;
    public int TranslationPending;
    public int TranslationInFlight;
    public long CacheHits;
    public long CacheMisses;

    public long UtterancesProcessed;
    public long DroppedUtterances;
    public long PartialsSkipped;
    public long TranslationFailures;

    public double LastAsrMs, AvgAsrMs;
    public double LastPartialAsrMs;
    public double LastTranslationMs, AvgTranslationMs;
    /// <summary>End of speech (VAD) → final English on screen.</summary>
    public double LastSpeechToTextMs, AvgSpeechToTextMs;
    /// <summary>End of speech (VAD) → translated subtitle on screen.</summary>
    public double LastTotalMs, AvgTotalMs;

    public void RecordAsr(double ms) { LastAsrMs = ms; AvgAsrMs = Ema(AvgAsrMs, ms); }
    public void RecordTranslation(double ms) { LastTranslationMs = ms; AvgTranslationMs = Ema(AvgTranslationMs, ms); }
    public void RecordSpeechToText(double ms) { LastSpeechToTextMs = ms; AvgSpeechToTextMs = Ema(AvgSpeechToTextMs, ms); }
    public void RecordTotal(double ms) { LastTotalMs = ms; AvgTotalMs = Ema(AvgTotalMs, ms); }

    public void ResetSession()
    {
        DroppedAudioBytes = 0; UtterancesProcessed = 0; DroppedUtterances = 0; PartialsSkipped = 0; TranslationFailures = 0;
        LastAsrMs = AvgAsrMs = LastPartialAsrMs = LastTranslationMs = AvgTranslationMs = 0;
        LastSpeechToTextMs = AvgSpeechToTextMs = LastTotalMs = AvgTotalMs = 0;
    }

    private static double Ema(double avg, double sample) => avg == 0 ? sample : avg * 0.8 + sample * 0.2;
}
