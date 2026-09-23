using System.Threading.Channels;
using ExtormSub.Core.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExtormSub.Core.History;

/// <summary>Persists pipeline sessions/segments on a background consumer so pipeline threads never touch the database.</summary>
public sealed class HistoryRecorder : IAsyncDisposable
{
    private readonly IHistoryStore _store;
    private readonly ILogger _log;
    private readonly Channel<Func<Task>> _work = Channel.CreateUnbounded<Func<Task>>(new() { SingleReader = true });
    private readonly Task _consumer;
    private SubtitlePipeline? _pipeline;

    public HistoryRecorder(IHistoryStore store, ILogger<HistoryRecorder>? log = null)
    {
        _store = store;
        _log = (ILogger?)log ?? NullLogger.Instance;
        _consumer = Task.Run(ConsumeAsync);
    }

    public bool Enabled { get; set; } = true;
    public string? AsrModel { get; set; }
    public string? TranslationModel { get; set; }

    public void Attach(SubtitlePipeline pipeline)
    {
        _pipeline = pipeline;
        pipeline.SessionStarted += OnSessionStarted;
        pipeline.SessionEnded += OnSessionEnded;
        pipeline.SegmentCompleted += OnSegment;
    }

    private void OnSessionStarted(SessionInfo s)
    {
        if (!Enabled) return;
        var record = new SessionRecord(s.Id, s.StartedAt, null, s.DeviceName, AsrModel, TranslationModel);
        _work.Writer.TryWrite(() => _store.StartSessionAsync(record));
    }

    private void OnSessionEnded(SessionInfo s)
    {
        if (!Enabled) return;
        var ended = DateTimeOffset.Now;
        _work.Writer.TryWrite(() => _store.EndSessionAsync(s.Id, ended));
    }

    private void OnSegment(SegmentRecord r)
    {
        if (!Enabled) return;
        _work.Writer.TryWrite(() => _store.AddSegmentAsync(r));
    }

    private async Task ConsumeAsync()
    {
        await foreach (var item in _work.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try { await item().ConfigureAwait(false); }
            catch (Exception ex) { _log.LogError(ex, "Writing history failed"); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_pipeline is { } p)
        {
            p.SessionStarted -= OnSessionStarted;
            p.SessionEnded -= OnSessionEnded;
            p.SegmentCompleted -= OnSegment;
        }
        _work.Writer.TryComplete();
        await _consumer.ConfigureAwait(false);
    }
}
