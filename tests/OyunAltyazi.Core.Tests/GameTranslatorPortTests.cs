using OyunAltyazi.Core;
namespace OyunAltyazi.Core.Tests;

public class NoiseAndPromptTests
{
    private static SubtitleText? Clean(params string[] lines) =>
        TextCleaner.Clean(new OcrResult(lines.Select((t, i) => new OcrLine(t, i * 0.2, 0.15)).ToList()));

    [Theory]
    [InlineData("Enter >")] [InlineData("Enter>")] [InlineData("> ")] [InlineData(">")] [InlineData("[F] ")] [InlineData("»")]
    [InlineData("<Price> text")] [InlineData("[F] Open door")] [InlineData("Press E to open")] [InlineData("Hold Shift")]
    [InlineData("• •")] [InlineData("<< >>")] [InlineData("R2 Swing")]
    public void Drops_hud_and_symbol_noise(string line) => Assert.Null(Clean(line));

    [Fact] public void Strips_stray_symbol_tokens_but_keeps_dialogue() =>
        Assert.Equal("Get to the chopper.", Clean("> Get to the » chopper. <")!.Text);

    [Fact] public void Keeps_real_dialogue_next_to_prompt() =>
        Assert.Equal("Move up, now.", Clean("Enter >", "Move up, now.")!.Text);

    [Theory]
    [InlineData("waters2aljd", "waters")] [InlineData("PS5 is here", "PS5 is here")] [InlineData("It costs $500", "It costs $500")]
    [InlineData("Wait for ou$3e now", "Wait for now")] [InlineData("Enter>", "Enter")]
    public void OcrNoise_cleans_words(string input, string expected) => Assert.Equal(expected, OcrNoise.Clean(input));

    [Fact] public void LooksGarbled_flags_junk_not_normal_text()
    {
        Assert.True(OcrNoise.LooksGarbled("Gtt solkolfege."));
        Assert.False(OcrNoise.LooksGarbled("We need to move now."));
    }

    [Fact] public void TextMatch_same_subtitle_vs_growth()
    {
        Assert.True(TextMatch.IsSameSubtitle("we need to move now before they come", "we need to move now before they come back"));
        Assert.False(TextMatch.IsSameSubtitle("we do not have time", "we do not have"));
        Assert.True(TextMatch.IsGrowth("We do not have time.", "We do not have"));
    }

    [Fact] public void Subtitle_block_drops_corner_menu_and_keeps_centred_subtitle()
    {
        var lines = new List<OcrLine>
        {
            new("MAP", 0.05, 0.1, 0.02, 0.08), new("HELP", 0.16, 0.1, 0.02, 0.08), new("SETTINGS", 0.27, 0.1, 0.02, 0.12),
            new("We need to move now.", 0.7, 0.15, 0.25, 0.5),
        };
        var c = TextCleaner.Clean(new OcrResult(lines))!;
        Assert.Equal("We need to move now.", c.Text);
    }
}

public class CoverAnchorTests
{
    private static readonly RegionF Region = new(0, 0.7, 1, 0.3);

    [Fact] public void Slightly_different_boxes_give_the_same_anchor()
    {
        var a = new CoverAnchor();
        var rnd = new Random(1);
        var bottoms = new List<double>(); var centres = new List<double>();
        for (int i = 0; i < 30; i++)
        {
            double jb = (rnd.NextDouble() - 0.5) * 0.02, jx = (rnd.NextDouble() - 0.5) * 0.04, w = 0.3 + rnd.NextDouble() * 0.3;
            var o = a.Stabilize(new RegionF(0.5 - w / 2 + jx, 0.85 + jb - 0.05, w, 0.1), Region);
            bottoms.Add(o.Y + o.H); centres.Add(o.X + o.W / 2);
        }
        Assert.True(bottoms.Max() - bottoms.Min() <= 0.0125 + 1e-9, $"bottom range {bottoms.Max() - bottoms.Min()}");
        Assert.All(centres, c => Assert.Equal(0.5, c, 6));   // locked on the region centre
    }

    [Fact] public void Always_covers_the_english_box()
    {
        var a = new CoverAnchor();
        var rnd = new Random(2);
        for (int i = 0; i < 40; i++)
        {
            var eng = new RegionF(0.2 + rnd.NextDouble() * 0.2, 0.8 + rnd.NextDouble() * 0.03, 0.3 + rnd.NextDouble() * 0.3, 0.05 + rnd.NextDouble() * 0.05);
            var o = a.Stabilize(eng, Region);
            Assert.True(o.X <= eng.X + 1e-9 && o.X + o.W >= eng.X + eng.W - 1e-9 && o.Y <= eng.Y + 0.04 * Region.H + 1e-9 && o.Y + o.H >= eng.Y + eng.H - 0.04 * Region.H - 1e-9);   // at most the cover slack (the overlay pad hides it)
        }
    }

    [Fact] public void Single_outlier_does_not_move_the_anchor_but_a_real_move_does()
    {
        var a = new CoverAnchor();
        var line = new RegionF(0.3, 0.85, 0.4, 0.05);              // bottom 0.90
        for (int i = 0; i < 5; i++) a.Stabilize(line, Region);
        double before = a.AnchorBottom!.Value;
        a.Stabilize(new RegionF(0.3, 0.72, 0.4, 0.05), Region);     // one far line (bottom 0.77, > 15% of region height)
        Assert.Equal(before, a.AnchorBottom!.Value, 6);
        a.Stabilize(new RegionF(0.3, 0.72, 0.4, 0.05), Region);     // second agreeing far line: subtitle moved
        Assert.Equal(0.77, a.AnchorBottom!.Value, 6);
    }

    [Fact] public void Top_anchored_game_one_vs_two_rows_pins_the_top_edge()
    {
        // CoD-like: 1 row = top .74..bottom .82, 2 rows = top .74..bottom .93 (top stays, bottom moves)
        var a = new CoverAnchor();
        var tops = new List<double>();
        for (int i = 0; i < 20; i++)
        {
            var o = a.Stabilize(new RegionF(0.3, 0.74, 0.4, i % 3 == 0 ? 0.19 : 0.08), Region);
            if (i >= 4) tops.Add(o.Y);
        }
        Assert.True(a.AnchorTop);
        Assert.True(tops.Max() - tops.Min() < 1e-9, "top edge must not move between 1- and 2-row lines");
    }

    [Fact] public void Cover_layout_top_anchor_pins_top_and_bottom_anchor_pins_bottom()
    {
        var eng = new DipRect(100, 500, 400, 40);
        var (_, yTop) = CoverLayout.Place(380, 120, eng, 1000, 800, anchorTop: true);
        var (_, yBottom) = CoverLayout.Place(380, 120, eng, 1000, 800, anchorTop: false);
        Assert.Equal(500 - CoverLayout.Pad, yTop, 6);
        Assert.Equal(540 + CoverLayout.Pad - 120, yBottom, 6);
    }

    [Fact] public void Off_centre_subtitle_keeps_its_own_stable_centre()
    {
        var a = new CoverAnchor();
        var o1 = a.Stabilize(new RegionF(0.05, 0.85, 0.3, 0.05), Region);
        var o2 = a.Stabilize(new RegionF(0.06, 0.85, 0.3, 0.05), Region);
        Assert.InRange(Math.Abs((o1.X + o1.W / 2) - (o2.X + o2.W / 2)), 0, 0.02);
    }
}

public class MultiSpeakerCleanerTests
{
    private static IReadOnlyList<SubtitleText> Scene(params string[] rows) =>
        TextCleaner.CleanScene(new OcrResult(rows.Select((t, i) => new OcrLine(t, 0.1 + i * 0.28, 0.25, 0.2, 0.6)).ToList()), out _, out _);

    [Fact] public void Two_stacked_speakers_become_two_utterances_in_screen_order()
    {
        var s = Scene("Price: Move up.", "Gaz: Copy that.");
        Assert.Equal([new SubtitleText("Price", "Move up."), new SubtitleText("Gaz", "Copy that.")], s);
    }

    [Fact] public void Three_speakers()
    {
        var s = Scene("Price: Move up now.", "Gaz: Copy that, moving.", "Soap: Right behind you.");
        Assert.Equal(["Price", "Gaz", "Soap"], s.Select(x => x.Speaker!).ToArray());
    }

    [Fact] public void Unlabelled_continuation_stays_with_previous_utterance()
    {
        var s = Scene("Price: Move up now and", "stay low until I say so.", "Gaz: Copy that.");
        Assert.Equal(2, s.Count);
        Assert.Equal("Move up now and stay low until I say so.", s[0].Text);
        Assert.Equal("Gaz", s[1].Speaker);
    }

    [Fact] public void Second_line_of_one_subtitle_starting_with_a_name_is_a_new_utterance()
    {
        var s = Scene("Price: Go now.", "Gaz: On my way.");
        Assert.Equal(2, s.Count);
    }

    [Fact] public void Name_in_its_own_ocr_box_beside_dialogue_still_splits_per_row()
    {
        var lines = new List<OcrLine>
        {
            new("Price:", 0.10, 0.2, 0.05, 0.12), new("Move up now.", 0.11, 0.2, 0.2, 0.5),
            new("Gaz:", 0.40, 0.2, 0.05, 0.10), new("Copy that, moving.", 0.41, 0.2, 0.2, 0.5),
        };
        var s = TextCleaner.CleanScene(new OcrResult(lines), out var box, out _);
        Assert.Equal(["Price", "Gaz"], s.Select(x => x.Speaker!).ToArray());
        Assert.True(box!.H > 0.4);   // box spans the whole English block
    }

    [Theory] [InlineData("Note: Something important happened here today.")] [InlineData("Objective: Reach the extraction point.")]
    public void Headings_are_not_speakers(string row) => Assert.Single(Scene(row));
}

public class MultiSpeakerPipelineTests
{
    private sealed class Src : IFrameSource
    {
        public event Action<Frame>? FrameArrived; public event Action<string>? Stopped { add { } remove { } }
        public void Start() { } public void Stop() { } public void Dispose() { }
        public void Send(Frame f) => FrameArrived?.Invoke(f);
    }
    private sealed class Ocr : IOcrEngine
    {
        public Dictionary<byte, string[]> Texts = new();
        public Task<OcrResult> ReadAsync(Frame f, CancellationToken ct)
        {
            var rows = Texts.GetValueOrDefault(f.Bgra[0], []);
            return Task.FromResult(new OcrResult(rows.Select((t, i) => new OcrLine(t, 0.05 + i * 0.3, 0.25, 0.2, 0.6)).ToList()));
        }
    }
    private sealed class Sink : ISubtitleSink
    {
        public readonly List<ShownUtterance[]> Calls = new(); public int Clears;
        public void Show(IReadOnlyList<ShownUtterance> u, RegionF? box = null, int lineCount = 1, bool anchorTop = false) { lock (Calls) Calls.Add(u.ToArray()); }
        public void Clear() => Interlocked.Increment(ref Clears);
        public ShownUtterance[][] Snap() { lock (Calls) return Calls.ToArray(); }
    }
    private sealed class Mem : ITranslationCache
    {
        public bool TryGet(string g, string s, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? t) { t = null; return false; }
        public void Put(string g, string s, string t, string p) { }
    }
    private sealed class Tr : ITranslator
    {
        public string Name => "t"; public readonly List<string> Seen = new();
        public Task<string> TranslateAsync(string text, TranslationContext ctx, CancellationToken ct) { lock (Seen) Seen.Add(text); return Task.FromResult("TR:" + text); }
        public string[] Snap() { lock (Seen) return Seen.ToArray(); }
    }
    private static Frame F(byte v, long t) => new(64, 16, 256, Enumerable.Repeat(v, 256 * 16).ToArray(), t);
    private static async Task WaitFor(Func<bool> c) { for (int i = 0; i < 300 && !c(); i++) await Task.Delay(10); }

    private sealed class Rig
    {
        public Src Src = new(); public Ocr Ocr = new(); public Sink Sink = new(); public Tr Tr = new(); public long Clock;
        public Rig() { new SubtitlePipeline(Src, Ocr, new TranslationChain(new Mem(), Tr), Sink, "g", new LatencyStats(), () => Clock, 10, () => new RegionF(0, 0.6, 1, 0.4)).Start(); }
        public void Send(byte v, int n = 2) { for (int i = 0; i < n; i++) { Src.Send(F(v, Clock)); Clock += 200; } }
    }

    [Fact] public async Task Two_speakers_are_shown_stacked_in_screen_order_with_their_own_names()
    {
        var r = new Rig();
        r.Ocr.Texts[10] = ["Price: Move up now.", "Gaz: Copy that, moving."];
        r.Send(10);
        await WaitFor(() => r.Sink.Snap().Any(c => c.Length == 2));
        var last = r.Sink.Snap().Last(c => c.Length == 2);
        Assert.Equal(["Price", "Gaz"], last.Select(u => u.Speaker!).ToArray());
        Assert.Equal(["TR:Move up now.", "TR:Copy that, moving."], last.Select(u => u.Text).ToArray());
    }

    [Fact] public async Task Three_speakers()
    {
        var r = new Rig();
        r.Ocr.Texts[10] = ["Price: Move up now.", "Gaz: Copy that, moving.", "Soap: Right behind you."];
        r.Send(10);
        await WaitFor(() => r.Sink.Snap().Any(c => c.Length == 3));
        Assert.Equal(["Price", "Gaz", "Soap"], r.Sink.Snap().Last(c => c.Length == 3).Select(u => u.Speaker!).ToArray());
    }

    [Fact] public async Task One_changes_other_stays_only_the_changed_one_is_retranslated_and_never_vanishes()
    {
        var r = new Rig();
        r.Ocr.Texts[10] = ["Price: Move up now.", "Gaz: Copy that, moving."];
        r.Ocr.Texts[200] = ["Price: Move up now.", "Gaz: I have eyes on the target."];
        r.Send(10);
        await WaitFor(() => r.Sink.Snap().Any(c => c.Length == 2));
        int before = r.Sink.Snap().Length;
        r.Send(200, 3);
        await WaitFor(() => r.Sink.Snap().Skip(before).Any(c => c.Any(u => u.Text.Contains("eyes"))));
        var after = r.Sink.Snap().Skip(before).ToArray();
        Assert.Contains(after, c => c.Any(u => u.Text.Contains("eyes")));
        Assert.All(after, c => { Assert.Equal(2, c.Length); Assert.Equal("TR:Move up now.", c[0].Text); });   // Price never flickers away
        Assert.Single(r.Tr.Snap(), "Move up now.");                                                          // and was translated once
    }

    [Fact] public async Task Sentences_are_never_joined_across_different_speakers()
    {
        var r = new Rig();
        r.Ocr.Texts[10] = ["Price: Move up now and"];
        r.Ocr.Texts[200] = ["Gaz: stay low until the signal."];
        r.Send(10); r.Send(200);
        await WaitFor(() => r.Tr.Snap().Length >= 2);
        await Task.Delay(100);
        Assert.DoesNotContain(r.Tr.Snap(), t => t.Contains("now and stay low"));
    }

    [Fact] public async Task A_speaker_that_leaves_is_dropped_and_the_other_remains()
    {
        var r = new Rig();
        r.Ocr.Texts[10] = ["Price: Move up now.", "Gaz: Copy that, moving."];
        r.Ocr.Texts[250] = ["Gaz: Copy that, moving."];
        r.Send(10);
        await WaitFor(() => r.Sink.Snap().Any(c => c.Length == 2));
        r.Send(250, 10);
        await WaitFor(() => r.Sink.Snap().Last().Length == 1);
        Assert.Equal("Gaz", r.Sink.Snap().Last().Single().Speaker);
    }
}

public class GibberishGateTests
{
    private static SubtitleText T(string s) => new(null, s);

    [Theory]
    [InlineData("There Ogedto egey billior f ug.")] [InlineData("toe oht houdS ago, terrorists stole a hipment")] [InlineData("atdi adi ans to ghange that.")]
    [InlineData("Gtt solkolfege wnkr tpl.")]
    public void Gibberish_fails_the_gate(string text) => Assert.False(TextQuality.Passes(text));

    [Theory]
    [InlineData("We need to move now before they come back.")] [InlineData("Commander Karim. Call me Alex.")] [InlineData("I don't care if I'm called a traitor.")]
    [InlineData("Copy that, moving to the extraction point.")] [InlineData("Hadir, this is my brother and lieutenant.")]
    public void Real_english_passes_the_gate(string text) => Assert.True(TextQuality.Passes(text));

    [Fact] public void Stabilizer_never_shows_gibberish()
    {
        var s = new Stabilizer();
        for (int i = 0; i < 12; i++) Assert.Null(s.Feed(T("There Ogedto egey billior f ug.")));
    }

    [Fact] public void Shown_line_is_not_swapped_to_a_worse_variant()
    {
        var s = new Stabilizer();
        Assert.NotNull(s.Feed(T("We need to move now before they come back.")));   // complete sentence: shown on the first read
        for (int i = 0; i < 6; i++) Assert.Null(s.Feed(T("We need to move now befor they come bak.")));   // worse re-read of the same line
    }

    [Fact] public void Better_read_replaces_once_only()
    {
        var s = new Stabilizer();
        var a = "Welcome to Urzikstan, we need you to wait for them";            // fine but fewer words than the full read
        s.Feed(T(a)); Assert.NotNull(s.Feed(T(a)));
        var better = "Welcome to Urzikstan, we need you to wait for them here, Captain Price.";
        Assert.NotNull(s.Feed(T(better)));                                     // one upgrade is allowed...
        var better2 = "Welcome to Urzikstan, we need you to wait for them here, Captain Price. Sit down now.";
        int shows = 0;
        for (int i = 0; i < 6; i++) if (s.Feed(T(better2)) is ShowLine) shows++;
        Assert.Equal(0, shows);                                                // ...a second one is not
    }
}
