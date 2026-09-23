using ExtormSub.Core.Settings;
using ExtormSub.Core.Subtitles;
using ExtormSub.Core.Translation;

namespace ExtormSub.Tests;

public class SubtitleSequencerTests
{
    private static readonly TimeSpan T0 = TimeSpan.Zero;
    private static SequencedSubtitle Final(long seq, string t = "fa") => new(seq, $"en{seq}", $"{t}{seq}", true, null);

    [Fact]
    public void Out_of_order_results_are_released_in_seq_order()
    {
        var s = new SubtitleSequencer(TimeSpan.FromSeconds(4));
        s.Open(148); s.Open(149); s.Open(150);

        Assert.Empty(s.Offer(Final(150), T0).Display); // waits for 148, 149
        Assert.Empty(s.Offer(Final(149), T0).Display);
        var released = s.Offer(Final(148), T0).Display;

        Assert.Equal([148L, 149L, 150L], released.Select(r => r.Seq));
        Assert.Equal(0, s.OpenCount);
    }

    [Fact]
    public void Stale_results_after_a_newer_seq_was_shown_are_discarded()
    {
        var s = new SubtitleSequencer(TimeSpan.FromSeconds(4));
        s.Open(1); s.Open(2);
        s.Offer(Final(1), T0);
        s.Offer(Final(2), T0);
        Assert.Empty(s.Offer(Final(1, "late"), T0).Display);
    }

    [Fact]
    public void Non_final_results_for_the_head_show_immediately_and_keep_it_open()
    {
        var s = new SubtitleSequencer(TimeSpan.FromSeconds(4));
        s.Open(1);
        var shown = s.Offer(new SequencedSubtitle(1, "I think", "فکر کنم", false, null), T0).Display;
        Assert.Single(shown);
        Assert.Equal(1, s.OpenCount);
    }

    [Fact]
    public void Empty_utterances_close_without_display_and_unblock_later_ones()
    {
        var s = new SubtitleSequencer(TimeSpan.FromSeconds(4));
        s.Open(1); s.Open(2);
        Assert.Empty(s.Offer(Final(2), T0).Display);
        Assert.Equal([2L], s.Close(1, T0).Display.Select(r => r.Seq));
    }

    [Fact]
    public void Stuck_head_is_abandoned_after_timeout_and_later_results_flow()
    {
        var s = new SubtitleSequencer(TimeSpan.FromSeconds(4));
        s.Open(1); s.Open(2);
        s.NoteOriginal(1, "hello");
        s.Offer(Final(2), TimeSpan.FromSeconds(10));        // blocked from t=10

        Assert.Empty(s.Poll(TimeSpan.FromSeconds(13)).Display); // not yet
        var output = s.Poll(TimeSpan.FromSeconds(14.1));
        Assert.Equal([1L], output.Abandoned);
        Assert.Equal([1L, 2L], output.Display.Select(d => d.Seq));
        Assert.True(output.Display[0].Abandoned);
        Assert.Null(output.Display[0].Translation);
        Assert.Empty(s.Offer(Final(1), TimeSpan.FromSeconds(15)).Display); // the late one is dropped
    }

    [Fact]
    public void Head_that_is_merely_slow_without_anyone_waiting_is_not_abandoned()
    {
        var s = new SubtitleSequencer(TimeSpan.FromSeconds(1));
        s.Open(1);
        Assert.Empty(s.Poll(TimeSpan.FromSeconds(100)).Abandoned);
    }

    [Fact]
    public void Randomized_completion_order_always_displays_monotonically()
    {
        var rng = new Random(42);
        for (int round = 0; round < 50; round++)
        {
            var s = new SubtitleSequencer(TimeSpan.FromSeconds(100));
            var seqs = Enumerable.Range(1, 30).Select(i => (long)i).ToList();
            foreach (var q in seqs) s.Open(q);
            var shown = new List<long>();
            foreach (var q in seqs.OrderBy(_ => rng.Next()))
                shown.AddRange(s.Offer(Final(q), T0).Display.Select(d => d.Seq));
            Assert.Equal(seqs, shown);
        }
    }
}

public class SubtitleExporterTests
{
    private static readonly SubtitleLine[] Lines =
    [
        new(TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(3.25), "Hello there.", "سلام."),
        new(TimeSpan.FromSeconds(3.0), TimeSpan.FromSeconds(3.4), "Short", null),
        new(TimeSpan.FromMinutes(61) + TimeSpan.FromMilliseconds(7), TimeSpan.FromMinutes(61) + TimeSpan.FromSeconds(2), "Late", "دیر"),
    ];

    [Fact]
    public void Srt_has_indices_comma_millis_and_both_lines()
    {
        var srt = SubtitleExporter.ToSrt(Lines, ExportContent.Both);
        Assert.StartsWith("1\n00:00:01,500 --> 00:00:03,000\nHello there.\nسلام.\n\n", srt); // clipped to next start
        Assert.Contains("2\n00:00:03,000 --> 00:00:04,200\nShort\n\n", srt);                 // padded to min duration
        Assert.Contains("3\n01:01:00,007 --> 01:01:02,000\nLate\nدیر", srt);
    }

    [Fact]
    public void Vtt_has_header_and_dot_millis()
    {
        var vtt = SubtitleExporter.ToVtt(Lines, ExportContent.Translation);
        Assert.StartsWith("WEBVTT\n\n00:00:01.500 --> 00:00:03.000\nسلام.\n\n", vtt);
        Assert.Contains("\nShort\n", vtt); // falls back to original when untranslated
    }

    [Fact]
    public void Txt_is_timestamped()
    {
        var txt = SubtitleExporter.ToText(Lines, ExportContent.Original);
        Assert.StartsWith("[00:00:01] Hello there.\n[00:00:03] Short\n[01:01:00] Late\n", txt);
    }
}

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("extormsub-test").FullName;
    private string PathFor => Path.Combine(_dir, "settings.json");

    [Fact]
    public void Missing_file_loads_defaults()
    {
        var store = new SettingsStore(PathFor);
        Assert.Equal("Ctrl+Alt+S", store.Current.Hotkeys.ToggleListening);
        Assert.Equal(DisplayMode.OriginalAndTranslation, store.Current.Overlay.DisplayMode);
        Assert.NotEmpty(store.Current.Glossary);
    }

    [Fact]
    public void Update_persists_and_raises_changed()
    {
        var store = new SettingsStore(PathFor);
        AppSettings? seen = null;
        store.Changed += (_, n) => seen = n;
        store.Update(s => s.Overlay.TranslationFontSize = 40);

        Assert.Equal(40, seen!.Overlay.TranslationFontSize);
        Assert.Equal(40, new SettingsStore(PathFor).Current.Overlay.TranslationFontSize);
        Assert.Contains("\"BottomCenter\"", File.ReadAllText(PathFor)); // enums as strings
    }

    [Fact]
    public void Partial_json_keeps_defaults_for_missing_fields_and_clamps_bad_values()
    {
        File.WriteAllText(PathFor, """
            { // comments allowed
              "overlay": { "translationFontSize": 9999, "position": "TopCenter" },
              "vad": { "speechThreshold": 7 },
            }
            """);
        var s = new SettingsStore(PathFor).Current;
        Assert.Equal(120, s.Overlay.TranslationFontSize);
        Assert.Equal(OverlayPosition.TopCenter, s.Overlay.Position);
        Assert.Equal(0.95f, s.Vad.SpeechThreshold);
        Assert.Equal(500, s.Vad.SilenceTimeoutMs);
    }

    [Fact]
    public void Corrupt_file_is_moved_aside_and_defaults_used()
    {
        File.WriteAllText(PathFor, "{ this is not json");
        var store = new SettingsStore(PathFor);
        Assert.Equal(2, store.Current.Translation.MaxRetries);
        Assert.Single(Directory.GetFiles(_dir, "settings.json.corrupt-*"));
    }

    [Fact]
    public void Snapshots_are_isolated_from_the_store()
    {
        var store = new SettingsStore(PathFor);
        var snap = store.Current;
        snap.Overlay.MaxLines = 5;
        Assert.Equal(2, store.Current.Overlay.MaxLines);
    }

    [Fact]
    public void Api_keys_are_never_part_of_the_settings_schema()
    {
        var json = SettingsStore.Serialize(new AppSettings());
        Assert.DoesNotContain("apikey", json, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => Directory.Delete(_dir, true);
}
