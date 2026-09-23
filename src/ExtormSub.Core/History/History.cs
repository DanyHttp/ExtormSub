namespace ExtormSub.Core.History;

public sealed record SessionRecord(
    string Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? Device,
    string? AsrModel,
    string? TranslationModel,
    int SegmentCount = 0,
    string? Preview = null);

public sealed record SegmentRecord(
    string SessionId,
    long Seq,
    DateTimeOffset Timestamp,
    TimeSpan Start,
    TimeSpan End,
    string OriginalText,
    string? TranslatedText,
    string? DetectedLanguage,
    float? Confidence,
    int? AsrLatencyMs,
    int? TranslationLatencyMs);

public interface IHistoryStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task StartSessionAsync(SessionRecord session, CancellationToken ct = default);
    Task EndSessionAsync(string sessionId, DateTimeOffset endedAt, CancellationToken ct = default);
    Task AddSegmentAsync(SegmentRecord segment, CancellationToken ct = default);
    Task<IReadOnlyList<SessionRecord>> ListSessionsAsync(string? search = null, CancellationToken ct = default);
    Task<IReadOnlyList<SegmentRecord>> GetSegmentsAsync(string sessionId, CancellationToken ct = default);
    Task DeleteSessionAsync(string sessionId, CancellationToken ct = default);
    Task ClearAsync(CancellationToken ct = default);
}
