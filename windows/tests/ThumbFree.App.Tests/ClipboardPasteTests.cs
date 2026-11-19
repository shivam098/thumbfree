using ThumbFree.App.Platform;

namespace ThumbFree.App.Tests;

/// <summary>Pasting through a fake clipboard: what's pasted and what's put back when the clipboard changes meanwhile.</summary>
public sealed class ClipboardPasteTests
{
    [Fact]
    public void Something_copied_while_the_paste_waits_is_kept_and_the_dictation_is_what_gets_pasted()
    {
        // Ctrl+C pressed while the paste waits for Ctrl to be let go.
        var clipboard = new FakeClipboard("copied before");
        var checks = 0;

        ClipboardPaste.Paste("Dictated words.", () =>
        {
            if (checks++ == 0)
            {
                clipboard.UserCopies("copied meanwhile");
            }
        }, clipboard);

        Assert.Equal(["Dictated words."], clipboard.Pasted);
        Assert.Equal("copied meanwhile", clipboard.Content);
    }

    [Fact]
    public void A_clipboard_that_keeps_changing_pastes_nothing_and_keeps_the_latest_copy()
    {
        var clipboard = new FakeClipboard("copied before");

        Assert.Throws<InvalidOperationException>(() =>
            ClipboardPaste.Paste("Dictated words.", () => clipboard.UserCopies("copied again"), clipboard));

        Assert.Empty(clipboard.Pasted);
        Assert.Equal("copied again", clipboard.Content);
    }

    [Fact]
    public void A_paste_stopped_before_the_keys_puts_the_clipboard_back()
    {
        var clipboard = new FakeClipboard("copied before");

        Assert.Throws<TimeoutException>(() =>
            ClipboardPaste.Paste("Dictated words.", () => throw new TimeoutException("moved to another box"), clipboard));

        Assert.Empty(clipboard.Pasted);
        Assert.Equal("copied before", clipboard.Content);
    }

    private sealed class FakeClipboard(string content) : ClipboardPaste.IPasteClipboard
    {
        public string? Content { get; private set; } = content;

        public uint Sequence { get; private set; } = 1;

        public List<string> Pasted { get; } = [];

        public void UserCopies(string text)
        {
            Content = text;
            Sequence++;
        }

        public (object? Previous, uint Ours) Put(string text)
        {
            var previous = Content;
            Content = text;
            Sequence++;
            return (previous, Sequence);
        }

        public void PressPaste() => Pasted.Add(Content!);

        public void Restore(object? previous, uint ours)
        {
            if (Sequence == ours)
            {
                Content = (string?)previous;
                Sequence++;
            }
        }
    }
}
