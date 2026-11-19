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
        Assert.Equal("", d.Pause("Let's meet on Friday at 3 pm,"));
        Assert.Equal("", d.Pause("actually 4 pm."));
        Assert.Equal("Let's meet on Friday at 4 pm.", d.Pending);
        Assert.Equal("Let's meet on Friday at 4 pm.", d.Pause("In the big room."));
        Assert.Equal(" In the big room.", d.Finish());
        Assert.DoesNotContain(d.Pieces, p => p.Contains("3 pm"));
    }

    [Theory]
    [InlineData("sorry,")]
    [InlineData("actually,")]
    [InlineData("I mean")]
    [InlineData("no wait")]
    public void A_correction_marker_in_a_phrase_of_its_own_waits_for_the_new_value(string marker)
    {
        var d = new Dictation("", SingleLine);
        Assert.Equal("", d.Pause("Meet me at the cafe at 6,"));
        Assert.Equal("", d.Pause(marker));
        Assert.Equal("", d.Pause("7 tonight."));
        Assert.Equal("Meet me at the cafe at 7 tonight. See you there.", d.Finish("See you there."));
        Assert.Equal(d.All, AtTheEnd(d));
    }

    [Fact]
    public void An_apology_at_the_end_of_a_phrase_does_not_hold_typing()
    {
        // Only a marker after a time, day or number can take a new value.
        var d = new Dictation("", SingleLine);
        Assert.Equal("", d.Pause("We missed the bus."));
        Assert.Equal("We missed the bus.", d.Pause("I'm so sorry,"));
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
    public void An_unfinished_sentence_waits_while_scratch_that_could_still_drop_it()
    {
        var d = new Dictation("", SingleLine);
        Assert.Equal("", d.Pause("I'll call you"));
        Assert.Equal("", d.Pause("tomorrow."));
        Assert.Equal("I'll email you tonight.", d.Finish("Scratch that. I'll email you tonight."));

        // Sentences before it are typed; the one "scratch that" can drop waits.
        var e = new Dictation("", SingleLine);
        Assert.Equal("", e.Pause("We shipped it."));
        Assert.Equal("We shipped it.", e.Pause("I'll call you"));
        Assert.Equal("", e.Pause("tomorrow."));
        Assert.Equal("", e.Pause("Scratch that."));
        Assert.Equal(" I'll email you tonight.", e.Finish("I'll email you tonight."));
        Assert.Equal("We shipped it. I'll email you tonight.", e.All);
        Assert.Equal(e.All, AtTheEnd(e));
    }

    [Fact]
    public void Without_written_punctuation_scratch_that_two_phrases_later_cannot_take_back_typed_text()
    {
        // The documented limit: unpunctuated speech has no boundary to hold back to, so phrases are typed as before.
        var d = new Dictation("", SingleLine);
        Assert.Equal("", d.Pause("kal call karunga"));
        Assert.Equal("Kal call karunga", d.Pause("parso milte hain"));
        Assert.Equal("", d.Pause("scratch that"));
        Assert.Equal(" Email karunga", d.Finish("email karunga"));
        Assert.True(d.Typing.FinishedWithoutLayout);
        Assert.Equal("Email karunga", AtTheEnd(d));
    }

    [Fact]
    public void A_filler_comma_leaves_speech_unpunctuated()
    {
        // "Um," is gone once the filler is removed, so it's no mark for "scratch that" to stop at.
        var d = new Dictation("", SingleLine);
        Assert.Equal("", d.Pause("Um, so the demo is ready"));
        Assert.Equal("So the demo is ready", d.Pause("and we can ship it"));
        Assert.Equal(" and we can ship it", d.Pause("on Friday after the review"));
        Assert.Equal(" on Friday after the review", d.Pause("with the whole team"));
        Assert.Equal(" with the whole team", d.Finish());
    }

    [Fact]
    public void Live_typing_goes_on_after_the_last_written_mark()
    {
        // A clause that runs on over two pauses without a mark is typed like unpunctuated speech.
        var d = new Dictation("", MultiLine);
        Assert.Equal("", d.Pause("We shipped it. and then we"));
        Assert.Equal("We shipped it. And then we", d.Pause("went to the office"));
        Assert.Equal(" went to the office", d.Pause("and had a long meeting"));
        Assert.Equal(" and had a long meeting about the launch", d.Finish("about the launch"));

        var e = new Dictation("", SingleLine);
        Assert.Equal("", e.Pause("Um so the demo is ready"));
        Assert.Equal("So the demo is ready", e.Pause("and we can ship it"));
        Assert.Equal(" and we can ship it", e.Pause("on Friday, after the review"));
        Assert.Equal(" on Friday, after the review", e.Pause("with the whole team"));
        Assert.Equal(" with the whole team and then celebrate", e.Finish("and then celebrate"));
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
    public void A_list_that_starts_in_a_later_phrase_is_held_while_nothing_is_typed()
    {
        var row = Vectors.Rows("layout.tsv").Select(r => r.Split('\t')).First(c => c[1].StartsWith("My plan:", StringComparison.Ordinal));
        string[] phrases = ["My plan:", "first, book the flights,", "second, find a hotel,", "and third, pack the bags."];
        Assert.Equal(row[1], string.Join(' ', phrases));

        var d = new Dictation("", MultiLine);
        Assert.Equal("", d.Pause(phrases[0]));
        Assert.False(d.Typing.HoldsAll);
        Assert.Equal("", d.Pause(phrases[1]));
        Assert.True(d.Typing.HoldsAll);
        Assert.Equal("", d.Pause(phrases[2]));
        Assert.Equal(Vectors.Unescape(row[2]), d.Finish(phrases[3]));

        var bullets = new Dictation("", MultiLine);
        Assert.Equal("", bullets.Pause("Meeting notes."));
        Assert.Equal("", bullets.Pause("Bullet point launch moved to Friday."));
        Assert.Equal("Meeting notes:\n- Launch moved to Friday.\n- Priya owns the docs.", bullets.Finish("Bullet point Priya owns the docs."));
    }

    [Fact]
    public void A_list_started_after_typed_text_is_laid_out_after_it()
    {
        var d = new Dictation("", MultiLine);
        Assert.Equal("", d.Pause("Meeting notes."));
        Assert.Equal("Meeting notes.", d.Pause("Two things."));
        Assert.Equal(" Two things.", d.Pause("Bullet point launch moved to Friday."));
        Assert.Equal("", d.Pause("Bullet point Priya owns the docs."));
        Assert.Equal("\n- Launch moved to Friday.\n- Priya owns the docs.", d.Finish());
        Assert.True(d.Typing.FinishedWithoutLayout);
        Assert.Equal("Meeting notes. Two things.\n- Launch moved to Friday.\n- Priya owns the docs.", d.All);
    }

    [Fact]
    public void When_the_final_text_would_change_typed_text_the_rest_is_typed_after_it()
    {
        var d = new Dictation("", MultiLine);
        Assert.Equal("", d.Pause("We have a plan."));
        Assert.Equal("We have a plan.", d.Pause("It's simple."));
        Assert.Equal(" It's simple.", d.Pause("First, we book the flights."));
        Assert.Equal(" First, we book the flights.", d.Pause("Second, we find a hotel."));

        // The third item would make it a numbered list, changing what's typed: from here it waits.
        Assert.Equal("", d.Pause("And third, we pack the bags."));
        Assert.Equal(" Second, we find a hotel. And third, we pack the bags.", d.Finish());
        Assert.True(d.Typing.FinishedWithoutLayout);
        Assert.False(d.Typing.PhrasesChanged);
        Assert.Equal("We have a plan. It's simple. First, we book the flights. Second, we find a hotel. And third, we pack the bags.", d.All);
        Assert.Equal(d.All, d.Typing.Typed);
    }

    [Fact]
    public void Phrases_that_do_not_begin_with_the_typed_ones_type_nothing_more()
    {
        var d = new Dictation("", MultiLine);
        d.Pause("We have a plan.");
        Assert.Equal("We have a plan.", d.Pause("It's simple."));

        // Against the contract: the app sends other phrases at the end. Typing more could repeat or garble what's there.
        Assert.Equal("", d.Typing.Finish(["We have another plan.", "It's simple."]));
        Assert.True(d.Typing.PhrasesChanged);
        Assert.Equal("We have another plan. It's simple.", d.Typing.FinalText);
        Assert.Equal("We have a plan.", d.Typing.Typed);
    }

    [Fact]
    public void A_single_line_field_gets_spaces_for_spoken_new_lines()
    {
        var d = new Dictation("", SingleLine);
        Assert.Equal("", d.Pause("First point."));
        Assert.Equal("", d.Pause("New line, second point."));
        Assert.Equal("First point Second point.", d.Pause("Third point."));
        Assert.Equal(" Third point.", d.Finish());
        Assert.DoesNotContain('\n', d.All);

        // A line break at the start becomes the joining space, not a second one.
        var e = new Dictation("We shipped it", SingleLine);
        Assert.Equal(" It was fun.", e.Finish("new line it was fun."));
    }

    [Fact]
    public void A_search_box_gets_no_full_stop()
    {
        var d = new Dictation("", new FieldInfo(Search: true));
        Assert.Equal("", d.Pause("weather in Delhi"));
        Assert.Equal("Weather in Delhi", d.Pause("this weekend full stop"));
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
    public void A_phrase_more_words_would_still_drop_is_not_typed()
    {
        // "thik hai na" alone is kept, but before more words it's a tag and dropped. The first one matched the kept second
        // one, so the text with the next phrase alone wasn't enough to tell.
        var d = new Dictation("", MultiLine);
        Assert.Equal("", d.Pause("thik hai na"));
        Assert.Equal("", d.Pause("thik hai na"));
        Assert.Equal("", d.Pause("I think"));
        Assert.Equal("I think we should go", d.Finish("we should go"));
    }

    [Fact]
    public void A_line_break_goes_out_with_the_words_after_it()
    {
        // A spoken mark right after spoken line breaks takes them back ("…it, we left"): the words are typed, the break waits.
        var d = new Dictation("", SingleLine);
        Assert.Equal("", d.Pause("We shipped it new line"));
        Assert.Equal("We shipped it", d.Pause("new line"));
        Assert.Equal(", we left", d.Finish("comma we left"));
        Assert.Equal("We shipped it, we left", d.All);
        Assert.False(d.Typing.FinishedWithoutLayout);
        Assert.Equal(d.All, AtTheEnd(d));

        var e = new Dictation("", MultiLine);
        Assert.Equal("", e.Pause("Dear team new line"));
        Assert.Equal("", e.Pause("the build is green."));
        Assert.Equal("Dear team\nThe build is green.", e.Pause("Thanks."));

        // A comma kept only for the line break after it waits with the break: at the end of the text it's dropped.
        var f = new Dictation("", MultiLine);
        Assert.Equal("", f.Pause("kal office mein meeting hai"));
        Assert.Equal("", f.Pause("comma"));
        Assert.Equal("Kal office mein meeting hai", f.Pause("new line"));
        Assert.Equal("", f.Pause("new line"));
        Assert.Equal("", f.Finish("comma"));
        Assert.Equal(f.All, AtTheEnd(f));
        Assert.False(f.Typing.FinishedWithoutLayout);
    }

    [Fact]
    public void Line_by_line_dictation_is_typed_live()
    {
        var list = new Dictation("", MultiLine);
        Assert.Equal("", list.Pause("milk new line"));
        Assert.Equal("Milk", list.Pause("eggs new line"));
        Assert.Equal("\nEggs", list.Pause("bread new line"));
        Assert.Equal("\nBread", list.Pause("butter new line"));
        Assert.Equal("\nButter\nCheese", list.Finish("cheese"));
        Assert.Equal(list.All, AtTheEnd(list));

        var note = new Dictation("", MultiLine);
        Assert.Equal("", note.Pause("Dear team, new line"));
        Assert.Equal("Dear team", note.Pause("the build is green, new line"));
        Assert.Equal("\nThe build is green", note.Pause("the release is on Friday, new line"));
        Assert.Equal("\nThe release is on Friday\nPriya", note.Finish("Priya"));
        Assert.Equal(note.All, AtTheEnd(note));

        var box = new Dictation("", SingleLine);
        Assert.Equal("", box.Pause("first item new line"));
        Assert.Equal("First item", box.Pause("second item new line"));
        Assert.Equal(" Second item", box.Pause("third item new line"));
        Assert.Equal(" Third item Fourth item", box.Finish("fourth item"));
        Assert.Equal(box.All, AtTheEnd(box));
    }

    [Fact]
    public void A_held_line_break_comes_first_when_the_rest_is_typed_apart()
    {
        // Unpunctuated, so "scratch that" takes back typed lines (the documented limit); the rest still starts on its own line.
        var d = new Dictation("", MultiLine);
        d.Pause("milk new line");
        Assert.Equal("Milk", d.Pause("eggs new line"));
        Assert.Equal("\nEggs", d.Pause("bread new line"));
        Assert.Equal("", d.Pause("scratch that"));
        Assert.Equal("\nButter", d.Finish("butter"));
        Assert.True(d.Typing.FinishedWithoutLayout);
    }

    [Fact]
    public void Empty_and_filler_only_phrases_are_ignored()
    {
        var d = new Dictation("", SingleLine);
        Assert.Equal("", d.Pause("See you on Friday at 3 pm,"));
        Assert.Equal("", d.Pause(""));
        Assert.Equal("", d.Pause("um"));
        Assert.Equal("", d.Pause("sorry, 4 pm."));
        Assert.Equal("", d.Pause("  "));
        Assert.Equal("See you on Friday at 4 pm.", d.Pause("Bye."));
        Assert.Equal(5, d.Typing.TypedPhrases);
        Assert.Equal(" Bye.", d.Finish());
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\r")]
    [InlineData("\v")]
    [InlineData("\u2028")]
    [InlineData("\u2029")]
    public void Line_ends_from_the_app_count_as_new_lines(string lineEnd)
    {
        Assert.Equal("And then we met.", new Dictation("Dear John," + lineEnd, MultiLine).Finish("and then we met."));

        // Already on a line of its own: no blank line before the list.
        Assert.Equal(
            "Things to do today:\n- Order groceries.\n- Go on a walk.\n- Call mom.",
            new Dictation("Notes" + lineEnd, MultiLine).Finish("things to do today: order groceries, go on a walk and call mom."));
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

    /// <summary>
    /// Typing live doesn't change the outcome unless <see cref="LiveTyping.FinishedWithoutLayout"/> says so. Over generated
    /// dictations (fixed seed), the same dictation typed at the end, by a separate <see cref="LiveTyping"/>, is compared
    /// with what was actually typed: where it begins with the pieces typed while speaking, <see cref="LiveTyping.Finish"/>
    /// must add exactly the rest; where it doesn't, the dictation must say it ended apart.
    /// </summary>
    [Fact]
    public void Typing_live_ends_with_the_text_typed_at_the_end()
    {
        var random = new Random(310);
        var formatter = new DictationFormatter();
        int typedLive = 0, apart = 0;
        const int Runs = 3000;
        for (var n = 0; n < Runs; n++)
        {
            var context = new LiveTypingContext(Pick(random, Befores), Pick(random, Fields), Live: true, SmartFormatting: random.Next(4) != 0);
            var phrases = Phrases(random, Rich);
            var atEnd = new LiveTyping(formatter, context with { Live = false }).Finish(phrases);
            var (live, rest, typing) = TypeLive(formatter, context, phrases);
            typedLive += live.Length > 0 ? 1 : 0;
            Assert.False(typing.PhrasesChanged);
            var what = $"{Describe(context, phrases)}\n  live:   {Vectors.Escape(live)}|{Vectors.Escape(rest)}\n  at end: {Vectors.Escape(atEnd)}";
            if (atEnd.StartsWith(live, StringComparison.Ordinal))
            {
                Assert.True(!typing.FinishedWithoutLayout && live + rest == atEnd, what);
            }
            else
            {
                Assert.True(typing.FinishedWithoutLayout, what);
                apart++;
            }
        }

        // Not true by holding everything back: most dictations type something while speaking, and few end apart.
        Assert.True(typedLive > Runs / 3, $"only {typedLive} of {Runs} typed anything live");
        Assert.True(apart < Runs / 20, $"{apart} of {Runs} ended apart");
    }

    /// <summary>Plain speech (no corrections, "scratch that", lists or emails): what's typed while speaking is always where the end result begins.</summary>
    [Fact]
    public void Plain_speech_never_ends_apart()
    {
        var random = new Random(311);
        var formatter = new DictationFormatter();
        for (var n = 0; n < 1500; n++)
        {
            var context = new LiveTypingContext(Pick(random, Befores), Pick(random, Fields), Live: true, SmartFormatting: random.Next(4) != 0);
            var phrases = Phrases(random, Plain);
            var atEnd = new LiveTyping(formatter, context with { Live = false }).Finish(phrases);
            var (live, rest, typing) = TypeLive(formatter, context, phrases);
            Assert.True(
                atEnd.StartsWith(live, StringComparison.Ordinal) && live + rest == atEnd && !typing.FinishedWithoutLayout,
                $"{Describe(context, phrases)}\n  live:   {Vectors.Escape(live)}|{Vectors.Escape(rest)}\n  at end: {Vectors.Escape(atEnd)}");
        }
    }

    /// <summary>
    /// Unpunctuated speech is typed while speaking, also after a filler comma ("Um, …") or one written mark, and when it's
    /// dictated line by line (every phrase ending in "new line"): by the time three phrases have finished, something is typed.
    /// </summary>
    [Fact]
    public void Unpunctuated_speech_is_typed_while_speaking()
    {
        var random = new Random(312);
        var formatter = new DictationFormatter();
        for (var n = 0; n < 2000; n++)
        {
            var lineByLine = random.Next(3) == 0;
            var phrases = Enumerable.Range(0, 4 + random.Next(4)).Select(_ => Pick(random, Unpunctuated) + (lineByLine ? " new line" : string.Empty)).ToArray();
            if (random.Next(3) == 1)
            {
                phrases[0] = "Um, " + phrases[0];
            }
            else if (random.Next(2) == 1)
            {
                // One mark after any word but the very last.
                var i = random.Next(phrases.Length - 1);
                var words = phrases[i].Split(' ');
                words[random.Next(words.Length)] += Pick(random, [",", "."]);
                phrases[i] = string.Join(' ', words);
            }

            var context = new LiveTypingContext(Pick<string?>(random, ["", null, "We shipped it", "Notes:\n"]), Pick(random, [SingleLine, MultiLine]), Live: true, SmartFormatting: random.Next(4) != 0);
            var typing = new LiveTyping(formatter, context);
            for (var k = 1; k < phrases.Length; k++)
            {
                typing.Update(phrases[..k], phrases[k]);
                Assert.True(k < 3 || typing.Typed.Length > 0, $"nothing typed after {k} phrases: {Describe(context, phrases)}");
            }
        }
    }

    private static readonly string?[] Befores = ["", null, "We shipped it", "Notes:\n", "Done. ", "Hello\r\n", "(", "line one\u2028", "  ", "Dear John,"];

    private static readonly FieldInfo[] Fields = [new(), new(MultiLine: true), new(MultiLine: true, Email: true), new(Search: true), new(Password: true)];

    private static readonly string[] Unpunctuated =
    [
        "so the demo is ready", "and we can ship it", "on Friday after the review", "with the whole team", "we should talk about the plan",
        "the build is green", "then we went home", "the release went out", "everyone was happy", "kal office mein meeting hai",
        "main 5 baje tak aa jaunga", "tum bhi aa jana",
    ];

    private static readonly string[] Plain =
    [
        "We shipped it.", "It was fun.", "The build is green.", "Can you check it?", "See you soon!", "When we shipped it,",
        "the team was happy.", "and then we celebrated.", "so the demo is ready", "can you check it", "we should go",
        "kal office mein meeting hai", "main 5 baje tak aa jaunga", "tum bhi aa jana", "thik hai na", "um", "", "  ", "Ok.",
        "haan to like", "for example,", "for example we could ship it.", "I think", "that's fine.", "new line", "comma",
        "full stop", "question mark", "I'm so sorry,", "Thanks for the help.", "Um, so the demo is ready",
        "We shipped it. and then we", "on Friday, after the review", "and had a long meeting",
    ];

    private static readonly string[] Rich =
    [
        .. Plain,
        "Meet me at the cafe at 6,", "sorry,", "actually", "I mean,", "no wait", "7 tonight.", "Friday at 3 pm", "actually 4 pm.",
        "on Monday, sorry, Tuesday.", "make it 5.", "scratch that.", "Scratch that, I'll email you.", "scratch that",
        "Things to do today:", "My plan:", "first, book the flights,", "second, find a hotel,", "and third, pack the bags.",
        "bullet point milk", "bullet point eggs", "next bullet bread", "order groceries, go on a walk,", "and call mom.",
        "Hey Tim,", "Hi Sam, the report is attached.", "Thanks, Ben.", "Best regards, Priya Sharma.", "Cheers!",
    ];

    private static T Pick<T>(Random random, T[] items) => items[random.Next(items.Length)];

    private static string[] Phrases(Random random, string[] pool) =>
        Enumerable.Range(0, 1 + random.Next(7)).Select(_ => Pick(random, pool)).ToArray();

    /// <summary>
    /// Phrase by phrase, the next one being spoken while the ones before have paused; the last one ends it. Returns what
    /// was typed while speaking and what <see cref="LiveTyping.Finish"/> added.
    /// </summary>
    private static (string Live, string AtFinish, LiveTyping Typing) TypeLive(DictationFormatter formatter, LiveTypingContext context, string[] phrases)
    {
        var typing = new LiveTyping(formatter, context);
        var live = new System.Text.StringBuilder();
        for (var k = 1; k < phrases.Length; k++)
        {
            live.Append(typing.Update(phrases[..k], phrases[k]).Type);
            Assert.Equal(live.ToString(), typing.Typed);
        }

        return (live.ToString(), typing.Finish(phrases), typing);
    }

    private static string Describe(LiveTypingContext context, string[] phrases) =>
        $"before={(context.TextBefore is null ? "null" : Vectors.Escape(context.TextBefore))} field={context.Field} smart={context.SmartFormatting}\n  phrases: {string.Join(" | ", phrases)}";

    /// <summary>The same dictation typed at the end.</summary>
    private static string AtTheEnd(Dictation d) => new LiveTyping(new DictationFormatter(), d.Context with { Live = false }).Finish(d.Finished);

    /// <summary>A dictation as the app drives it: each phrase is spoken word by word (the partial), then a pause finishes it.</summary>
    private sealed class Dictation(string? textBefore, FieldInfo field, bool live = true, bool smart = true)
    {
        private readonly List<string> _finished = [];

        public LiveTypingContext Context { get; } = new(textBefore, field, live, smart);

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
