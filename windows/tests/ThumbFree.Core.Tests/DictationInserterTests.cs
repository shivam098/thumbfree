using ThumbFree.Core;

namespace ThumbFree.Core.Tests;

public class DictationInserterTests
{
    private sealed class FakeDesktop : IDesktop
    {
        public WindowInfo? Foreground { get; set; } = new(1, 1, "slack", "Slack");

        public FieldCheck Field { get; set; } = FieldCheck.Safe;

        public bool? EndsWithText { get; set; } = false;

        public List<string> Actions { get; } = [];

        public WindowInfo? GetForegroundWindow() => Foreground;

        public FieldCheck CheckFocusedField() => Field;

        public bool? FocusedTextEndsWithNonWhitespace() => EndsWithText;

        public void TypeText(string text) => Actions.Add($"type:{text}");

        public void PressKeys(string chord) => Actions.Add($"keys:{chord}");
    }

    private readonly FakeDesktop _desktop = new();
    private readonly List<StatusUpdate> _statuses = [];
    private readonly DictationInserter _inserter;

    public DictationInserterTests()
    {
        _inserter = new DictationInserter(_desktop, new DictationFormatter(), new Dictionary<string, string> { ["notepad"] = "Enter" });
        _inserter.Status += _statuses.Add;
    }

    [Fact]
    public void Inserts_cleaned_text()
    {
        Assert.True(_inserter.Insert("um so the demo is ready comma can you check it question mark"));
        Assert.Equal(["type:So the demo is ready, can you check it?"], _desktop.Actions);
        Assert.Equal(StatusKind.Success, _statuses[^1].Kind);
    }

    [Fact]
    public void Adds_a_space_after_existing_text()
    {
        _desktop.EndsWithText = true;
        _inserter.Insert("Second sentence.");
        Assert.Equal(["type: Second sentence."], _desktop.Actions);
    }

    [Fact]
    public void Chat_apps_get_shift_enter_and_editors_get_enter_for_new_lines()
    {
        _inserter.Insert("first new line second");
        Assert.Equal(["type:First", "keys:Shift+Enter", "type:Second"], _desktop.Actions);

        _desktop.Actions.Clear();
        _desktop.Foreground = new WindowInfo(2, 2, "Notepad", "Untitled - Notepad");
        _inserter.Insert("first new line second");
        Assert.Equal(["type:First", "keys:Enter", "type:Second"], _desktop.Actions);
    }

    [Theory]
    [InlineData(FieldCheck.Password)]
    [InlineData(FieldCheck.Elevated)]
    public void Refuses_password_fields_and_admin_windows(FieldCheck check)
    {
        _desktop.Field = check;
        Assert.False(_inserter.Insert("secret"));
        Assert.Empty(_desktop.Actions);
        Assert.Equal(StatusKind.Warning, _statuses[^1].Kind);
    }

    [Fact]
    public void Unknown_fields_are_allowed_because_the_key_press_is_consent()
    {
        _desktop.Field = FieldCheck.Unknown;
        Assert.True(_inserter.Insert("hello"));
    }

    [Fact]
    public void No_window_means_nothing_is_typed()
    {
        _desktop.Foreground = null;
        Assert.False(_inserter.Insert("hello"));
        Assert.Empty(_desktop.Actions);
    }

    [Fact]
    public void Only_filler_words_types_nothing()
    {
        Assert.False(_inserter.Insert("um uh"));
        Assert.Empty(_desktop.Actions);
    }

    [Fact]
    public void Repeat_last_types_the_previous_text_again()
    {
        Assert.False(_inserter.RepeatLast());
        _inserter.Insert("hello there");
        _desktop.Actions.Clear();
        Assert.True(_inserter.RepeatLast());
        Assert.Equal(["type:Hello there"], _desktop.Actions);
    }

    [Fact]
    public void No_space_goes_before_punctuation()
    {
        // Shared rule (Insertion.WithSpacing): before, this typed " ?" after the word.
        _desktop.EndsWithText = true;
        _inserter.Insert("question mark");
        Assert.Equal(["type:?"], _desktop.Actions);
    }

    [Fact]
    public void With_the_field_and_the_text_before_it_the_shared_pipeline_runs()
    {
        var multiLine = new FieldInfo(MultiLine: true);
        LiveTypingContext Context(string? before, FieldInfo field) => new(before, field, Live: true, SmartFormatting: true);

        // The text before decides the space and the lowercase "and", not the desktop's guess.
        Assert.True(_inserter.Insert("and then we celebrated", Context("We shipped it", multiLine)));
        Assert.Equal(["type: and then we celebrated"], _desktop.Actions);
        Assert.Equal("and then we celebrated", _inserter.LastText);

        // Enter in a single-line box would send or search.
        _desktop.Actions.Clear();
        _inserter.Insert("first new line second", Context("", new FieldInfo()));
        Assert.Equal(["type:First Second"], _desktop.Actions);

        _desktop.Actions.Clear();
        _inserter.Insert("weather in Delhi full stop", Context("", new FieldInfo(Search: true)));
        Assert.Equal(["type:Weather in Delhi"], _desktop.Actions);

        _desktop.Actions.Clear();
        _inserter.Insert("my shopping list: milk, eggs, bread and butter.", Context("", multiLine));
        Assert.Equal(["type:My shopping list:", "keys:Shift+Enter", "type:- Milk.", "keys:Shift+Enter", "type:- Eggs.", "keys:Shift+Enter", "type:- Bread and butter."], _desktop.Actions);

        // Unknown text before: the desktop's answer adds the space, as without a context.
        _desktop.Actions.Clear();
        _desktop.EndsWithText = true;
        _inserter.Insert("see you there", Context(null, multiLine));
        Assert.Equal(["type: See you there"], _desktop.Actions);
    }

    [Fact]
    public void A_password_field_in_the_context_is_refused()
    {
        Assert.False(_inserter.Insert("open sesame", new LiveTypingContext("", new FieldInfo(Password: true), Live: true, SmartFormatting: true)));
        Assert.Empty(_desktop.Actions);
        Assert.Equal(StatusKind.Warning, _statuses[^1].Kind);
    }

    [Fact]
    public void Live_pieces_are_typed_as_given_and_only_problems_are_reported()
    {
        _desktop.EndsWithText = true;
        Assert.True(_inserter.TypePrepared("Hello there."));
        Assert.True(_inserter.TypePrepared("\n\nThanks,\nBen"));
        Assert.False(_inserter.TypePrepared(string.Empty));
        Assert.Equal(["type:Hello there.", "keys:Shift+Enter", "keys:Shift+Enter", "type:Thanks,", "keys:Shift+Enter", "type:Ben"], _desktop.Actions);
        Assert.Empty(_statuses);
        Assert.Null(_inserter.LastText);

        _desktop.Field = FieldCheck.Password;
        Assert.False(_inserter.TypePrepared("more"));
        Assert.Equal(StatusKind.Warning, _statuses[^1].Kind);
    }

    [Fact]
    public void Live_pieces_for_a_password_field_are_refused()
    {
        // The field read when dictation started says password, even if the desktop can't tell now.
        Assert.False(_inserter.TypePrepared("open sesame", new FieldInfo(Password: true)));
        Assert.Empty(_desktop.Actions);
        Assert.Equal(StatusKind.Warning, _statuses[^1].Kind);
        Assert.True(_inserter.TypePrepared("Hello", new FieldInfo(MultiLine: true)));
    }
}
