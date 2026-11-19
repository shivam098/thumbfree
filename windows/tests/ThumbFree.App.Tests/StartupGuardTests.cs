namespace ThumbFree.App.Tests;

public sealed class StartupGuardTests
{
    private readonly List<string> _shown = [];
    private int _exits;

    [Fact]
    public void A_failure_before_the_tray_icon_exists_says_what_went_wrong_and_exits()
    {
        var guard = new StartupGuard(_shown.Add, () => _exits++);

        Assert.True(guard.Handle(new UnauthorizedAccessException("Access to the path 'settings.json' is denied."), trayExists: false));

        Assert.Equal(1, _exits);
        var message = Assert.Single(_shown);
        Assert.StartsWith("ThumbFree couldn't start: Access to the path 'settings.json' is denied.", message, StringComparison.Ordinal);
        Assert.Contains(AppPaths.LogsDir, message, StringComparison.Ordinal);
    }

    [Fact]
    public void It_exits_even_when_the_error_dialog_fails()
    {
        var guard = new StartupGuard(_ => throw new InvalidOperationException("no desktop"), () => _exits++);

        Assert.True(guard.Handle(new IOException("disk"), trayExists: false));

        Assert.Equal(1, _exits);
    }

    [Fact]
    public void Once_the_tray_icon_exists_the_usual_handling_applies()
    {
        var guard = new StartupGuard(_shown.Add, () => _exits++);

        Assert.False(guard.Handle(new IOException("disk"), trayExists: true));

        Assert.Empty(_shown);
        Assert.Equal(0, _exits);
    }
}
