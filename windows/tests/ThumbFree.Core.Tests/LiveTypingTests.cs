using ThumbFree.Core;

namespace ThumbFree.Core.Tests;

/// <summary>
/// Live typing on the desktop (PLAN 3.10): finished phrases are typed at pauses, and nothing typed is ever changed, so
/// every piece is only added after the ones before it.
/// </summary>
public class LiveTypingTests
{
    private static readonly FieldInfo SingleLine = new();
    private static readonly FieldInfo MultiLine = new(MultiLine: true);
    private static readonly FieldInfo EmailBody = new(MultiLine: true, Email: true);

    [Fact]
    public void Phrases_are_typed_one_pause_late()
    {
        var d = new Dictation("", SingleLine);
        Assert.Equal("", d.Pause("hello there."));
        Assert.Equal("Hello there.", d.Pending);
        Assert.Equal("Hello there.", d.Pause("how are you?"));
        Assert.Equal("How are you?", d.Pending);
        Assert.Equal(" How are you?", d.Pause("see you soon."));
        Assert.Equal(" See you soon.", d.Finish());

        Assert.Equal("Hello there. How are you? See you soon.", d.All);
        Assert.Equal(d.All, d.Typing.Typed);
        Assert.Equal(3, d.Typing.TypedPhrases);
        Assert.False(d.Typing.FinishedWithoutLayout);
    }

    [Fact]
    public void The_pill_shows_what_is_said_but_not_typed()
    {
        var d = new Dictation("", SingleLine);
        d.Pause("hello there.");
        Assert.Equal("Hello there. How are", d.Typing.Update(d.Finished, "how are").Pending);
        d.Pause("how are you?");
        Assert.Equal("How are you? See", d.Typing.Update(d.Finished, "see").Pending);
    }

    [Fact]
    public void A_correction_after_a_pause_changes_the_phrase_before_it_is_typed()
    {
        var d = new Dictation("", MultiLine);
        Assert.Equal("", d.Pause("let's meet on Friday at 3 pm"));
        Assert.Equal("", d.Pause("actually 4 pm"));
        Assert.Equal("Let's meet on Friday at 4 pm", d.Pending);
        Assert.Equal("Let's meet on Friday at 4 pm", d.Pause("in the big room."));
        Assert.Equal(" in the big room.", d.Finish());
        Assert.DoesNotContain(d.Pieces, p => p.Contains("3 pm"));
    }

    [Fact]
    public void A_correction_in_the_last_phrase_is_made_when_finishing()
    {
        var d = new Dictation("", SingleLine);
        Assert.Equal("", d.Pause("Friday at 3 pm"));
        Assert.Equal("Friday at 4 pm", d.Finish("actually 4 pm"));
        Assert.Equal(["Friday at 4 pm"], d.Pieces);
    }

    [Fact]
    public void Scratch_that_after_a_pause_drops_the_phrase_before_it_is_typed()
    {
        var d = new Dictation("", SingleLine);
        Assert.Equal("", d.Pause("I'll call you tomorrow."));
        Assert.Equal("", d.Pause("scratch that. I'll email you."));
        Assert.Equal("I'll email you.", d.Pause("talk soon."));
        Assert.Equal(" Talk soon.", d.Finish());
        Assert.DoesNotContain(d.Pieces, p => p.Contains("call"));
    }

    [Fact]
    public void An_email_is_held_and_typed_laid_out_when_finished()
    {
        var row = Vectors.Rows("layout.tsv").Select(r => r.Split('\t')).First(c => c[0] == "mse" && c[1].StartsWith("Hey Tim", StringComparison.Ordinal));
        string[] phrases = ["Hey Tim, would love to connect.", "Are you available on Friday at 3 PM...", "actually 4 PM?", "Thanks, Ben."];
        Assert.Equal(row[1], string.Join(' ', phrases));

        var d = new Dictation("", EmailBody);
        foreach (var phrase in phrases[..^1])
        {
            Assert.Equal("", d.Pause(phrase));
        }

        Assert.True(d.Typing.HoldsAll);
        Assert.Equal(Vectors.Unescape(row[2]), d.Finish(phrases[^1]));
        Assert.Single(d.Pieces);
    }

    [Fact]
    public void A_greeting_in_a_non_email_field_is_typed_live()
    {
        // Outside email apps an email also needs a signed name, which only comes at the end: chat messages keep live typing.
        var d = new Dictation("", MultiLine);
        Assert.Equal("", d.Pause("Hey Tim, are you free tonight?"));
        Assert.False(d.Typing.HoldsAll);
        Assert.Equal("Hey Tim, are you free tonight?", d.Pause("Want to grab dinner?"));
        Assert.Equal(" Want to grab dinner?", d.Finish());
    }

    [Fact]
    public void A_greeting_in_an_email_field_holds_the_dictation()
    {
        var d = new Dictation("", EmailBody);
        Assert.Equal("", d.Pause("Hey Tim, are you free tonight?"));
        Assert.True(d.Typing.HoldsAll);
        Assert.Equal("", d.Pause("Want to grab dinner?"));
        Assert.Equal("Hey Tim,\n\nAre you free tonight? Want to grab dinner?", d.Finish());
    }

    [Fact]
    public void A_sign_off_waits_for_the_end_and_goes_on_its_own_lines()
    {
        var d = new Dictation("", EmailBody);
        Assert.Equal("", d.Pause("Can you send me the build logs?"));
        Assert.Equal("Can you send me the build logs?", d.Pause("Thanks, Ben."));
        Assert.False(d.Typing.HoldsAll);
        Assert.Equal("\n\nThanks,\nBen", d.Finish());
        Assert.Equal("Can you send me the build logs?\n\nThanks,\nBen", d.All);
    }

    [Fact]
    public void A_list_is_held_and_typed_laid_out_when_finished()
    {
        var d = new Dictation("", MultiLine);
        Assert.Equal("", d.Pause("Things to do today:"));
        Assert.True(d.Typing.HoldsAll);
        Assert.Equal("", d.Pause("order groceries, go on a walk,"));
        Assert.Equal("Things to do today: order groceries, go on a walk", d.Pending);
        Assert.Equal("Things to do today:\n- Order groceries.\n- Go on a walk.\n- Call mom.", d.Finish("and call mom."));

        // After other text, the list starts on a line of its own.
        var after = new Dictation("Plans for Saturday", MultiLine);
        after.Pause("things to do today:");
        Assert.Equal("\nThings to do today:\n- Order groceries.\n- Call mom.", after.Finish("order groceries and call mom."));
    }

    [Fact]
    public void A_list_started_after_typed_text_is_laid_out_after_it()
    {
        var d = new Dictation("", MultiLine);
        Assert.Equal("", d.Pause("Meeting notes."));
        Assert.Equal("Meeting notes.", d.Pause("Bullet point launch moved to Friday."));
        Assert.Equal("", d.Pause("Bullet point Priya owns the docs."));
        Assert.Equal("\n- Launch moved to Friday.\n- Priya owns the docs.", d.Finish());
        Assert.True(d.Typing.FinishedWithoutLayout);
        Assert.Equal("Meeting notes.\n- Launch moved to Friday.\n- Priya owns the docs.", d.All);
    }

    [Fact]
    public void When_the_final_text_would_change_typed_text_the_rest_is_typed_after_it()
    {
        var d = new Dictation("", MultiLine);
        Assert.Equal("", d.Pause("We have a plan."));
        Assert.Equal("We have a plan.", d.Pause("First, we book the flights."));
        Assert.Equal(" First, we book the flights.", d.Pause("Second, we find a hotel."));

        // The third item would make it a numbered list, changing what's typed: from here it waits.
        Assert.Equal("", d.Pause("And third, we pack the bags."));
        Assert.Equal(" Second, we find a hotel. And third, we pack the bags.", d.Finish());
        Assert.True(d.Typing.FinishedWithoutLayout);
        Assert.Equal("We have a plan. First, we book the flights. Second, we find a hotel. And third, we pack the bags.", d.All);
        Assert.Equal(d.All, d.Typing.Typed);
    }

    [Fact]
    public void A_single_line_field_gets_spaces_for_spoken_new_lines()
    {
        var d = new Dictation("", SingleLine);
        Assert.Equal("", d.Pause("first point new line"));
        Assert.Equal("First point ", d.Pause("second point."));
        Assert.Equal("Second point.", d.Finish());
        Assert.Equal("First point Second point.", d.All);
    }

    [Fact]
    public void A_search_box_gets_no_full_stop()
    {
        var d = new Dictation("", new FieldInfo(Search: true));
        Assert.Equal("", d.Pause("weather in Delhi"));
        Assert.Equal("Weather in Delhi", d.Pause("this weekend."));
        Assert.Equal(" this weekend", d.Finish());
    }

    [Fact]
    public void Dictation_continues_the_sentence_before_the_cursor()
    {
        var d = new Dictation("We shipped it", MultiLine);
        Assert.Equal("", d.Pause("and then we celebrated."));
        Assert.Equal(" and then we celebrated.", d.Pause("It was fun."));
        Assert.Equal(" It was fun.", d.Finish());
        Assert.Equal("and then we celebrated. It was fun.", d.Typing.Text);
    }

    [Fact]
    public void Unknown_text_before_the_cursor_starts_a_sentence_without_email_layout()
    {
        var d = new Dictation(null, EmailBody);
        Assert.Equal("", d.Pause("hey Tim, would love to connect."));
        Assert.False(d.Typing.HoldsAll);
        Assert.Equal("Hey Tim, would love to connect.", d.Pause("Thanks, Ben."));
        Assert.Equal(" Thanks, Ben.", d.Finish());
        Assert.Equal(d.All, d.Typing.Text);
    }

    [Fact]
    public void With_live_typing_off_everything_is_typed_at_the_end()
    {
        var d = new Dictation("", SingleLine, live: false);
        Assert.Equal("", d.Pause("hello there."));
        Assert.Equal("", d.Pause("how are you?"));
        Assert.Equal("Hello there. How are you?", d.Pending);
        Assert.True(d.Typing.HoldsAll);
        Assert.Equal("Hello there. How are you? See you.", d.Finish("see you."));
        Assert.Single(d.Pieces);
    }

    [Fact]
    public void Hinglish_without_punctuation_waits_for_the_next_phrase()
    {
        var d = new Dictation("", MultiLine);
        Assert.Equal("", d.Pause("kal office mein meeting hai thik hai na"));

        // On its own the tag ends a sentence ("… hai."); followed by more it's a comma: wait for that.
        Assert.Equal("", d.Pause("main 5 baje tak aa jaunga"));
        Assert.Equal("Kal office mein meeting hai, main 5 baje tak aa jaunga", d.Pause("tum bhi aa jana"));
        Assert.Equal(" tum bhi aa jana", d.Finish());
        Assert.Equal("Kal office mein meeting hai, main 5 baje tak aa jaunga tum bhi aa jana", d.All);
    }

    [Fact]
    public void Empty_and_filler_only_phrases_are_ignored()
    {
        var d = new Dictation("", SingleLine);
        Assert.Equal("", d.Pause("see you on Friday at 3 pm"));
        Assert.Equal("", d.Pause(""));
        Assert.Equal("", d.Pause("um"));
        Assert.Equal("", d.Pause("sorry, 4 pm"));
        Assert.Equal("", d.Pause("  "));
        Assert.Equal("See you on Friday at 4 pm", d.Pause("bye."));
        Assert.Equal(5, d.Typing.TypedPhrases);
        Assert.Equal(" bye.", d.Finish());
    }

    [Fact]
    public void Nothing_is_typed_twice_while_a_phrase_is_spoken()
    {
        var d = new Dictation("", SingleLine);
        d.Pause("hello there.");
        d.Pause("how are you?");
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal("", d.Typing.Update(d.Finished, "see").Type);
        }

        Assert.Equal(["Hello there."], d.Pieces);
    }

    [Theory]
    [InlineData("Hey Tim,", true)]
    [InlineData("Hi", true)]
    [InlineData("Good morning team: quick update", true)]
    [InlineData("Hello. How are you?", false)]
    [InlineData("History is fun", false)]
    [InlineData("Shopping list bullet point milk", true)]
    [InlineData("Bullet point milk", true)]
    [InlineData("We should bite the bullet and ship it.", false)]
    [InlineData("My plan: first, book the flights,", true)]
    [InlineData("At first I was scared,", false)]
    [InlineData("Things to do today", true)]
    [InlineData("The agenda was long, boring,", false)]
    [InlineData("I need to finish the list, then email Bob.", false)]
    [InlineData("Dear team\nThe build is green.", false)]
    public void Live_typing_holds_what_may_become_an_email_or_a_list(string start, bool expected) =>
        Assert.Equal(expected, Layout.MayLayOut(start, new LayoutContext(MultiLine: true, AtStart: true, Email: true)));

    [Fact]
    public void Greetings_hold_only_at_the_start_of_an_email_and_nothing_holds_in_a_single_line_field()
    {
        Assert.True(Layout.MayLayOut("Hey Tim,", new LayoutContext(MultiLine: true, AtStart: true, Email: true)));
        Assert.False(Layout.MayLayOut("Hey Tim,", new LayoutContext(MultiLine: true, AtStart: true, Email: false)));
        Assert.False(Layout.MayLayOut("Hey Tim,", new LayoutContext(MultiLine: true, AtStart: false, Email: true)));
        Assert.True(Layout.MayLayOut("Things to do today", new LayoutContext(MultiLine: true, AtStart: false, Email: false)));
        Assert.False(Layout.MayLayOut("Things to do today", new LayoutContext(MultiLine: false, AtStart: true, Email: true)));
    }

    /// <summary>A dictation as the app drives it: each phrase is spoken word by word (the partial), then a pause finishes it.</summary>
    private sealed class Dictation(string? textBefore, FieldInfo field, bool live = true, bool smart = true)
    {
        private readonly List<string> _finished = [];

        public LiveTyping Typing { get; } = new(new DictationFormatter(), new LiveTypingContext(textBefore, field, live, smart));

        public IReadOnlyList<string> Finished => _finished;

        /// <summary>Every piece typed into the app, in order.</summary>
        public List<string> Pieces { get; } = [];

        public string All => string.Concat(Pieces);

        /// <summary>The pill after the latest update.</summary>
        public string Pending { get; private set; } = string.Empty;

        /// <summary>Speaks <paramref name="phrase"/>, then pauses; returns what is typed at the pause.</summary>
        public string Pause(string phrase)
        {
            Speak(phrase);
            _finished.Add(phrase);
            var step = Typing.Update(_finished, string.Empty);
            Pending = step.Pending;
            if (step.Type.Length > 0)
            {
                Pieces.Add(step.Type);
            }

            return step.Type;
        }

        /// <summary>Ends the dictation, optionally after one more phrase that never paused; returns what is typed at the end.</summary>
        public string Finish(string? tail = null)
        {
            if (tail is not null)
            {
                Speak(tail);
                _finished.Add(tail);
            }

            var rest = Typing.Finish(_finished);
            if (rest.Length > 0)
            {
                Pieces.Add(rest);
            }

            return rest;
        }

        private void Speak(string phrase)
        {
            var words = phrase.Split(' ');
            for (var n = 1; n <= words.Length; n++)
            {
                var step = Typing.Update(_finished, string.Join(' ', words[..n]));
                Assert.Equal(string.Empty, step.Type);
                Pending = step.Pending;
            }
        }
    }
}
