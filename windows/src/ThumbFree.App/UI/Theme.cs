using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ThumbFree.App.UI;

/// <summary>
/// The shared look of the app window and the guide: ThumbFree's colours (the Android app's palette), cards, headings,
/// flat buttons, switches, radio buttons, text boxes and progress bars. Light and Windows 11-like; built in code with
/// no UI library.
/// </summary>
internal static class Theme
{
    public static readonly SolidColorBrush Background = Hex("#F4F4F6");
    public static readonly SolidColorBrush Sidebar = Hex("#EBEBF0");
    public static readonly SolidColorBrush Card = Hex("#FFFFFF");
    public static readonly SolidColorBrush Border = Hex("#E2E2E8");
    public static readonly SolidColorBrush Text = Hex("#1B1B1F");
    public static readonly SolidColorBrush Muted = Hex("#5F6368");
    public static readonly SolidColorBrush Accent = Hex("#5B4BDB");
    public static readonly SolidColorBrush AccentSoft = Hex("#ECEAFD");
    public static readonly SolidColorBrush Ok = Hex("#1E8E3E");
    public static readonly SolidColorBrush Warning = Hex("#9A5B00");
    public static readonly SolidColorBrush Danger = Hex("#B3261E");
    public static readonly SolidColorBrush Track = Hex("#E0E0E6");

    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");

    /// <summary>The guide's big titles, a serif like the Android guide's.</summary>
    public static readonly FontFamily TitleFont = new("Georgia");

    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static readonly Color ReadyColour = Color("#1E8E3E");
    public static readonly Color ListeningColour = Color("#E5484D");
    public static readonly Color BusyColour = Color("#E8A200");
    public static readonly Color PausedColour = Color("#9AA0A6");

    private static readonly ControlTemplate FlatButton = Template(
        """
        <ControlTemplate TargetType="Button">
          <Border x:Name="Chrome" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}"
                  BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="6" Padding="{TemplateBinding Padding}" SnapsToDevicePixels="True">
            <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center" RecognizesAccessKey="True" />
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Chrome" Property="Opacity" Value="0.5" /></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """);

    private static readonly ControlTemplate SwitchTemplate = Template(
        """
        <ControlTemplate TargetType="CheckBox">
          <StackPanel Orientation="Horizontal" Background="Transparent">
            <ContentPresenter VerticalAlignment="Center" Margin="0,0,10,0" RecognizesAccessKey="True" />
            <Grid Width="40" Height="20" VerticalAlignment="Center">
              <Border x:Name="Track" CornerRadius="10" BorderThickness="1" BorderBrush="#85858E" Background="#FFFFFF" />
              <Ellipse x:Name="Knob" Width="12" Height="12" Fill="#5F6368" HorizontalAlignment="Left" Margin="4,0,0,0" />
            </Grid>
          </StackPanel>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Track" Property="BorderBrush" Value="#5B4BDB" /></Trigger>
            <Trigger Property="IsChecked" Value="True">
              <Setter TargetName="Track" Property="Background" Value="#5B4BDB" />
              <Setter TargetName="Track" Property="BorderBrush" Value="#5B4BDB" />
              <Setter TargetName="Knob" Property="Fill" Value="#FFFFFF" />
              <Setter TargetName="Knob" Property="Margin" Value="24,0,0,0" />
            </Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45" /></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """);

    private static readonly ControlTemplate RadioTemplate = Template(
        """
        <ControlTemplate TargetType="RadioButton">
          <Grid Background="Transparent">
            <Grid.ColumnDefinitions><ColumnDefinition Width="Auto" /><ColumnDefinition Width="*" /></Grid.ColumnDefinitions>
            <Grid Width="20" Height="20" VerticalAlignment="Top" Margin="0,1,12,0">
              <Ellipse x:Name="Ring" Stroke="#85858E" StrokeThickness="1.5" Fill="#FFFFFF" />
              <Ellipse x:Name="Dot" Width="8" Height="8" Fill="#FFFFFF" Visibility="Collapsed" />
            </Grid>
            <ContentPresenter Grid.Column="1" VerticalAlignment="Center" RecognizesAccessKey="True" />
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Ring" Property="Stroke" Value="#5B4BDB" /></Trigger>
            <Trigger Property="IsChecked" Value="True">
              <Setter TargetName="Ring" Property="Fill" Value="#5B4BDB" />
              <Setter TargetName="Ring" Property="Stroke" Value="#5B4BDB" />
              <Setter TargetName="Dot" Property="Visibility" Value="Visible" />
            </Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.45" /></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """);

    private static readonly ControlTemplate TextBoxTemplate = Template(
        """
        <ControlTemplate TargetType="TextBox">
          <Border x:Name="Chrome" CornerRadius="6" BorderThickness="1" BorderBrush="#D2D2DA" Background="{TemplateBinding Background}" SnapsToDevicePixels="True">
            <ScrollViewer x:Name="PART_ContentHost" Focusable="False" Margin="{TemplateBinding Padding}"
                          HorizontalScrollBarVisibility="Hidden" VerticalScrollBarVisibility="{TemplateBinding VerticalScrollBarVisibility}" />
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="#AFAFB9" /></Trigger>
            <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="#5B4BDB" /></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="Chrome" Property="Opacity" Value="0.55" /></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
        """);

    private static readonly ControlTemplate ProgressTemplate = Template(
        """
        <ControlTemplate TargetType="ProgressBar">
          <Grid MinHeight="4" SnapsToDevicePixels="True">
            <Border x:Name="PART_Track" CornerRadius="2" Background="#E0E0E6" />
            <Border x:Name="PART_Indicator" CornerRadius="2" Background="#5B4BDB" HorizontalAlignment="Left" />
          </Grid>
        </ControlTemplate>
        """);

    public static SolidColorBrush Hex(string hex)
    {
        var brush = new SolidColorBrush(Color(hex));
        brush.Freeze();
        return brush;
    }

    public static Color Color(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>An image from the app's Assets folder (works from tests too, which run under another executable).</summary>
    public static BitmapImage Asset(string name)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri($"pack://application:,,,/ThumbFree;component/Assets/{name}");
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>The app icon with all its sizes, so Windows picks the sharpest for the title bar and taskbar.</summary>
    public static BitmapFrame AppIcon() => BitmapFrame.Create(new Uri("pack://application:,,,/ThumbFree;component/Assets/app.ico"));

    /// <summary>A Segoe Fluent Icons glyph (Segoe MDL2 Assets on Windows 10).</summary>
    public static TextBlock Icon(string glyph, double size = 16, Brush? brush = null) => new()
    {
        Text = glyph,
        FontFamily = IconFont,
        FontSize = size,
        Foreground = brush ?? Text,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock Heading(string text, double size = 26) => new()
    {
        Text = text,
        FontSize = size,
        FontFamily = DisplayFont,
        FontWeight = FontWeights.SemiBold,
        Foreground = Text,
        Margin = new Thickness(0, 0, 0, 4),
    };

    /// <summary>A card's title.</summary>
    public static TextBlock Title(string text, double size = 15) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = FontWeights.SemiBold,
        Foreground = Text,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 6),
    };

    public static TextBlock Label(string text, double size = 13, Brush? brush = null, bool wrap = true) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = brush ?? Text,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
    };

    public static Border CardOf(UIElement child, Brush? background = null, Thickness? padding = null) => new()
    {
        Background = background ?? Card,
        BorderBrush = Border,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = padding ?? new Thickness(18, 16, 18, 16),
        Margin = new Thickness(0, 0, 0, 12),
        Child = child,
    };

    /// <summary>A thin line between rows of a card.</summary>
    public static Border Divider() => new()
    {
        Height = 1,
        Background = Border,
        Margin = new Thickness(0, 12, 0, 12),
    };

    /// <summary>A flat button: <paramref name="primary"/> is filled (red with <paramref name="danger"/>); otherwise white, red text with <paramref name="danger"/>.</summary>
    public static Button Button(string text, RoutedEventHandler onClick, bool primary = false, bool danger = false)
    {
        var (normal, hover, pressed) = (primary, danger) switch
        {
            (true, true) => (Color("#B3261E"), Color("#9C2019"), Color("#851B15")),
            (true, false) => (Color("#5B4BDB"), Color("#4C3DC7"), Color("#3F31B0")),
            _ => (Color("#FFFFFF"), Color("#F1F1F5"), Color("#E4E4EA")),
        };
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0),
            FontSize = 13,
            Cursor = System.Windows.Input.Cursors.Hand,
            Foreground = primary ? Brushes.White : danger ? Danger : Text,
            BorderBrush = primary ? new SolidColorBrush(normal) : Border,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Animate(button, normal, hover, pressed);
        button.Click += onClick;
        return button;
    }

    /// <summary>A text-only button in the accent colour, for "Show the guide again" and other links.</summary>
    public static Button Link(string text, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(6, 3, 6, 3),
            Margin = new Thickness(-6, 0, 8, 0),
            FontSize = 13,
            Cursor = System.Windows.Input.Cursors.Hand,
            Foreground = Accent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Animate(button, Color("#00ECEAFD"), Color("#ECEAFD"), Color("#DCD8FB"));
        button.Click += onClick;
        return button;
    }

    /// <summary>One of a set of choices (English / Hindi + English): filled when chosen.</summary>
    public static Button Choice(string text, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 0, 8, 0),
            FontSize = 13,
            Cursor = System.Windows.Input.Cursors.Hand,
            BorderThickness = new Thickness(1),
            BorderBrush = Border,
            Foreground = Text,
        };
        Animate(button, Color("#FFFFFF"), Color("#F1F1F5"), Color("#E4E4EA"));
        button.Click += onClick;
        return button;
    }

    public static void SetChosen(Button button, bool chosen)
    {
        SetRestColor(button, chosen ? Color("#5B4BDB") : Color("#FFFFFF"));
        button.Foreground = chosen ? Brushes.White : Text;
        button.BorderBrush = chosen ? Accent : Border;
        button.FontWeight = chosen ? FontWeights.SemiBold : FontWeights.Normal;
    }

    /// <summary>A Windows 11-style on/off switch ("On"/"Off" beside it). <paramref name="name"/> is read by screen readers.</summary>
    public static CheckBox Switch(bool on, string name)
    {
        var toggle = new CheckBox
        {
            IsChecked = on,
            Template = SwitchTemplate,
            FontSize = 13,
            Foreground = Text,
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Content = on ? "On" : "Off",
        };
        System.Windows.Automation.AutomationProperties.SetName(toggle, name);
        toggle.Checked += (_, _) => toggle.Content = "On";
        toggle.Unchecked += (_, _) => toggle.Content = "Off";
        return toggle;
    }

    public static RadioButton Radio(string group, object content) => new()
    {
        GroupName = group,
        Content = content,
        Template = RadioTemplate,
        Cursor = System.Windows.Input.Cursors.Hand,
        Foreground = Text,
        FontSize = 13,
    };

    public static TextBox TextBox(string text = "", bool multiLine = false, double minHeight = 0)
    {
        var box = new TextBox
        {
            Text = text,
            Template = TextBoxTemplate,
            Padding = new Thickness(8, 6, 8, 6),
            FontSize = 13,
            Foreground = Text,
            Background = Card,
            AcceptsReturn = multiLine,
            TextWrapping = multiLine ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = multiLine ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
            MinHeight = minHeight,
            VerticalContentAlignment = multiLine ? VerticalAlignment.Top : VerticalAlignment.Center,
        };
        SpellCheck.SetIsEnabled(box, false);
        return box;
    }

    /// <summary>A text box with grey hint text while it's empty.</summary>
    public static Grid WithPlaceholder(TextBox box, string placeholder)
    {
        var hint = new TextBlock
        {
            Text = placeholder,
            FontSize = box.FontSize,
            Foreground = Hex("#8B8B93"),
            Margin = new Thickness(box.Padding.Left + 3, box.Padding.Top + 1, 0, 0),
            IsHitTestVisible = false,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = box.AcceptsReturn ? VerticalAlignment.Top : VerticalAlignment.Center,
        };
        void Update() => hint.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) => Update();
        Update();
        var grid = new Grid();
        grid.Children.Add(box);
        grid.Children.Add(hint);
        return grid;
    }

    public static ProgressBar Progress() => new()
    {
        Template = ProgressTemplate,
        Height = 4,
        Minimum = 0,
        Maximum = 1,
    };

    /// <summary>A key as it looks on the keyboard ("Ctrl", "Win").</summary>
    public static Border KeyCap(string key) => new()
    {
        Background = Hex("#F7F7FA"),
        BorderBrush = Hex("#CFCFD8"),
        BorderThickness = new Thickness(1, 1, 1, 2),
        CornerRadius = new CornerRadius(5),
        Padding = new Thickness(7, 1, 7, 2),
        Margin = new Thickness(0, 0, 4, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = key, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Text },
    };

    /// <summary>"Ctrl+Win" as two key caps with a plus between.</summary>
    public static StackPanel Keys(string chord)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var parts = chord.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                panel.Children.Add(new TextBlock { Text = "+", Foreground = Muted, FontSize = 12, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            }

            panel.Children.Add(KeyCap(parts[i]));
        }

        return panel;
    }

    /// <summary>
    /// A page's content: as wide as the window allows up to <paramref name="maxWidth"/>, against the left edge, so every
    /// card on the page lines up.
    /// </summary>
    public static Grid Column(UIElement content, double maxWidth, Thickness margin)
    {
        var grid = new Grid { Margin = margin };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = maxWidth });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(content);
        return grid;
    }

    /// <summary>At most <paramref name="lines"/> lines of <paramref name="text"/>, with "…" when there's more.</summary>
    public static string FirstLines(string text, int lines)
    {
        var all = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return all.Length <= lines ? text : string.Join("\n", all.Take(lines)).TrimEnd() + " …";
    }

    /// <summary>A row with a title and a description on the left and a control (switch, button) on the right.</summary>
    public static Grid Row(string title, string? detail, UIElement control)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        texts.Children.Add(Label(title, 14));
        if (detail is not null)
        {
            var hint = Label(detail, 12, Muted);
            hint.Margin = new Thickness(0, 2, 0, 0);
            texts.Children.Add(hint);
        }

        grid.Children.Add(texts);
        if (control is FrameworkElement element)
        {
            element.VerticalAlignment = VerticalAlignment.Center;
        }

        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>
    /// A flat, rounded look (no default Windows chrome) whose background eases between its normal, hover and pressed
    /// colours. <paramref name="normal"/> can be changed later with <see cref="SetRestColor"/> (a chosen item).
    /// </summary>
    public static void Animate(Button button, Color normal, Color hover, Color pressed)
    {
        var brush = Motion.Live(normal);
        button.Background = brush;
        button.Template = FlatButton;
        button.Tag = normal;
        Color Rest() => button.Tag is Color c ? c : normal;
        button.MouseEnter += (_, _) => Motion.ToColor(brush, hover);
        button.MouseLeave += (_, _) => Motion.ToColor(brush, Rest());
        button.PreviewMouseLeftButtonDown += (_, _) => Motion.ToColor(brush, pressed, 60);
        button.PreviewMouseLeftButtonUp += (_, _) => Motion.ToColor(brush, button.IsMouseOver ? hover : Rest(), 120);
    }

    /// <summary>Changes the colour a button rests at (and eases to it unless the mouse is over it).</summary>
    public static void SetRestColor(Button button, Color color)
    {
        button.Tag = color;
        if (!button.IsMouseOver && button.Background is SolidColorBrush brush && !brush.IsFrozen)
        {
            Motion.ToColor(brush, color);
        }
    }

    private static ControlTemplate Template(string xaml) => (ControlTemplate)XamlReader.Parse(
        xaml.Replace("<ControlTemplate ", "<ControlTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" ", StringComparison.Ordinal));
}
