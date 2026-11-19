using System.Windows;
using ThumbFree.App.UI;

namespace ThumbFree.App.Tests;

public sealed class WindowFitTests : IDisposable
{
    /// <summary>1920×1080 at 150% less the taskbar.</summary>
    private static readonly Rect Laptop = new(0, 0, 1280, 672);

    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void A_window_taller_than_the_work_area_is_made_to_fit_it()
    {
        var (size, min) = WindowFit.Fit(new Size(1060, 740), new Size(860, 560), Laptop);

        Assert.Equal(new Size(1060, 640), size);
        Assert.Equal(new Size(860, 560), min);
    }

    [Fact]
    public void The_minimum_size_shrinks_too_when_the_screen_is_smaller_still()
    {
        var (size, min) = WindowFit.Fit(new Size(860, 740), new Size(700, 600), new Rect(0, 0, 800, 600));

        Assert.Equal(new Size(768, 568), size);
        Assert.Equal(new Size(700, 568), min);
    }

    [Fact]
    public void A_big_screen_keeps_the_usual_size()
    {
        var (size, min) = WindowFit.Fit(new Size(1060, 740), new Size(860, 560), new Rect(0, 0, 1920, 1032));

        Assert.Equal(new Size(1060, 740), size);
        Assert.Equal(new Size(860, 560), min);
    }

    [Theory]
    [InlineData(100, 20, 100, 20)]
    [InlineData(100, 50, 100, 32)]
    [InlineData(1500, -20, 680, 0)]
    [InlineData(-300, 400, 0, 32)]
    [InlineData(-2000, 100, 0, 32)]
    public void A_window_is_moved_only_as_far_as_needed_to_be_inside(double left, double top, double expectedLeft, double expectedTop) =>
        Assert.Equal(new Point(expectedLeft, expectedTop), WindowFit.Inside(new Rect(left, top, 600, 640), Laptop));

    [Fact]
    public void A_window_bigger_than_the_area_keeps_its_title_bar_on_screen() =>
        Assert.Equal(new Point(-1920, 0), WindowFit.Inside(new Rect(-1800, -50, 2400, 1200), new Rect(-1920, 0, 1920, 1032)));

    [Fact]
    public void The_app_window_and_the_guide_fit_a_1080p_screen_at_150_percent() => UiThread.Run(() =>
    {
        var services = TestServices.Create(_folder);

        var window = new MainWindow(services, _ => { }, Laptop);
        Assert.Equal(640, window.Height);
        Assert.Equal(560, window.MinHeight);
        Assert.Equal(1060, window.Width);

        var guide = new GuideWindow(services, () => { }, Laptop);
        Assert.Equal(640, guide.Height);
        Assert.Equal(600, guide.MinHeight);
        Assert.Equal(860, guide.Width);
        guide.View.Detach();
    });

    [Fact]
    public void The_guide_keeps_its_buttons_in_view_when_it_is_short() => UiThread.Run(() =>
    {
        var services = TestServices.Create(_folder);
        var guide = new GuideView(services, () => { });
        guide.Go(GuideStep.Data);

        Render.Save(guide, "guide-9-data-short", 700, 420);

        foreach (var label in new[] { "Back", "Finish" })
        {
            var button = Render.ButtonWith(guide, label);
            var bottom = button.TranslatePoint(new Point(0, button.ActualHeight), guide).Y;
            Assert.InRange(bottom, button.ActualHeight, 420);
        }

        guide.Detach();
    });
}
