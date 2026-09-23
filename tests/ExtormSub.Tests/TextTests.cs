using ExtormSub.Core.ASR;
using ExtormSub.Core.Text;

namespace ExtormSub.Tests;

public class TranscriptStabilizerTests
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);
    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Growing_partials_are_never_translated()
    {
        var s = new TranscriptStabilizer(Debounce);
        string[] words = ["I", "I think", "I think we", "I think we should", "I think we should leave"];
        for (int i = 0; i < words.Length; i++)
            Assert.False(s.OnPartial(1, words[i], Ms(i * 800))!.Value.Translate);
    }

    [Fact]
    public void Unchanged_text_after_debounce_becomes_stable_once()
    {
        var s = new TranscriptStabilizer(Debounce);
        Assert.False(s.OnPartial(1, "That's sick.", Ms(0))!.Value.Translate);
        Assert.False(s.OnPartial(1, "That's sick", Ms(200))!.Value.Translate);   // same key, too soon
        Assert.True(s.OnPartial(1, "that's  sick", Ms(450))!.Value.Translate);   // normalized equal, debounce passed
        Assert.False(s.OnPartial(1, "That's sick.", Ms(1200))!.Value.Translate); // already released
    }

    [Fact]
    public void Final_is_always_eligible_and_closes_the_seq()
    {
        var s = new TranscriptStabilizer(Debounce);
        s.OnPartial(1, "I think we", Ms(0));
        var d = s.OnFinal(1, "I think we should leave.", Ms(100))!.Value;
        Assert.True(d.IsFinal);
        Assert.True(d.Translate);
        Assert.Null(s.OnPartial(1, "late partial", Ms(200))); // stale after final
    }

    [Fact]
    public void Empty_final_is_not_translated()
    {
        var s = new TranscriptStabilizer(Debounce);
        Assert.False(s.OnFinal(3, "  ", Ms(0))!.Value.Translate);
    }

    [Fact]
    public void Older_seq_results_are_ignored_after_a_newer_seq_starts()
    {
        var s = new TranscriptStabilizer(Debounce);
        s.OnPartial(5, "new", Ms(0));
        Assert.Null(s.OnPartial(4, "old", Ms(10)));
        Assert.Null(s.OnFinal(4, "old", Ms(10)));
    }
}

public class TranscriptFilterTests
{
    [Theory]
    [InlineData("[Music]", "")]
    [InlineData("(laughs) Hello there", "Hello there")]
    [InlineData("♪ la la ♪", "la la")]
    [InlineData("Subtitles by the Amara.org community", "")]
    [InlineData("  Hello   world  ", "Hello world")]
    [InlineData("...", "")]
    public void Cleans_annotations_and_hallucinations(string raw, string expected) =>
        Assert.Equal(expected, TranscriptFilter.Clean(raw, noSpeechProbability: 0.0f));

    [Fact]
    public void Suspicious_short_phrases_survive_only_when_whisper_is_confident()
    {
        Assert.Equal("Thank you.", TranscriptFilter.Clean("Thank you.", 0.05f));
        Assert.Equal("", TranscriptFilter.Clean("Thank you.", 0.4f));
        Assert.Equal("", TranscriptFilter.Clean("Anything at all", 0.9f));
    }
}

public class GlossaryTests
{
    private static Glossary Sample() => new(Glossary.Defaults());

    [Theory]
    [InlineData("i asked chat gpt about it", "i asked ChatGPT about it")]
    [InlineData("Chat-GPT is by open ai", "ChatGPT is by OpenAI")]
    [InlineData("push it to github and git hub", "push it to GitHub and GitHub")]
    [InlineData("openai's model", "OpenAI's model")]
    [InlineData("claude said", "Claude said")]
    public void Normalizes_aliases_and_casing(string input, string expected) =>
        Assert.Equal(expected, Sample().ApplyToTranscript(input));

    [Fact]
    public void Matches_whole_words_only()
    {
        var g = new Glossary([new GlossaryEntry { Term = "Rust" }]);
        Assert.Equal("Rusty trusted Rust", g.ApplyToTranscript("Rusty trusted rust"));
    }

    [Fact]
    public void Parse_and_format_round_trip()
    {
        var text = "# comment\nChatGPT; chat gpt, chat g p t\nKubernetes; k8s; کوبرنتیز\nOpenAI\n\n";
        var entries = Glossary.Parse(text);
        Assert.Equal(3, entries.Count);
        Assert.Equal(["chat gpt", "chat g p t"], entries[0].Aliases);
        Assert.Equal("کوبرنتیز", entries[1].Translation);
        Assert.Empty(entries[2].Aliases);
        var again = Glossary.Parse(Glossary.Format(entries));
        Assert.Equivalent(entries, again);
    }

    [Fact]
    public void FindIn_returns_hints_and_prompt_lists_terms()
    {
        var g = Sample();
        Assert.Equal(["ChatGPT"], g.FindIn("I love ChatGPT").Select(e => e.Term));
        Assert.StartsWith("OpenAI, ChatGPT", g.AsrPrompt);
    }

    [Theory]
    [InlineData("Hello World.", "hello world")]
    [InlineData("  hello \n world…", "hello world")]
    [InlineData("Really?", "really?")]
    public void Cache_key_normalization(string input, string key) => Assert.Equal(key, TextNormalizer.ForKey(input));
}
