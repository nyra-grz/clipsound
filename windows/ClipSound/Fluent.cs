using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClipSound;

/// <summary>Feste Farbe pro Sound, Clan und Person – gleiche Reihenfolge und gleicher Hash wie am Mac
/// (Palette in Glass.swift), damit eine Kachel überall gleich aussieht.</summary>
public static class Palette
{
    // blau, lila, pink, orange, grün, türkis, indigo, rot – in den Windows-Akzentfarben
    private static readonly Color[] Colors =
    {
        Color.FromRgb(0x00, 0x78, 0xD4), Color.FromRgb(0x88, 0x64, 0xD9), Color.FromRgb(0xE3, 0x00, 0x8C), Color.FromRgb(0xF7, 0x63, 0x0C),
        Color.FromRgb(0x10, 0x89, 0x3E), Color.FromRgb(0x00, 0x99, 0xBC), Color.FromRgb(0x5C, 0x2E, 0x91), Color.FromRgb(0xD1, 0x34, 0x38),
    };

    public static readonly Color Blue = Colors[0], Purple = Colors[1], Orange = Colors[3], Green = Colors[4], Indigo = Colors[6];

    public static Color For(string key)
    {
        uint h = 0;
        foreach (char c in key) unchecked { h = h * 31 + c; }
        return Colors[h % (uint)Colors.Length];
    }

    private static Color Mix(Color a, Color b, double by) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * by), (byte)(a.G + (b.G - a.G) * by), (byte)(a.B + (b.B - a.B) * by));

    private static Brush Diagonal(Color from, Color to)
    {
        var brush = new LinearGradientBrush(from, to, new Point(0, 0), new Point(1, 1));
        brush.Freeze();
        return brush;
    }

    /// <summary>Gedämpfter Verlauf für Sound-Kacheln – viele helle Flächen nebeneinander blenden sonst</summary>
    public static Brush Tile(Color c) => Diagonal(Mix(c, System.Windows.Media.Colors.Black, 0.06), Mix(c, System.Windows.Media.Colors.Black, 0.34));

    /// <summary>Verlauf für Symbole, Avatare und Clan-Zeichen: oben heller, unten satter</summary>
    public static Brush Gradient(Color c) => Diagonal(Mix(c, System.Windows.Media.Colors.White, 0.12), Mix(c, System.Windows.Media.Colors.Black, 0.18));

    public static Brush Solid(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }
}

/// <summary>Bausteine für Seiten, die im Code aufgebaut werden (Konto, Clan, Lobby). Farben kommen aus dem
/// Fluent-Theme, damit hell/dunkel stimmt.</summary>
public static class Ui
{
    public static TextBlock Text(string text, double size = 14, FontWeight? weight = null, string brush = "TextFillColorPrimaryBrush", bool wrap = true)
    {
        var t = new TextBlock
        {
            Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    public static TextBlock Secondary(string text, double size = 14) => Text(text, size, brush: "TextFillColorSecondaryBrush");
    public static TextBlock Caption(string text) => Text(text, 12, brush: "TextFillColorSecondaryBrush");

    public static TextBlock PageTitle(string text)
    {
        var t = new TextBlock { Text = text };
        t.SetResourceReference(FrameworkElement.StyleProperty, "PageTitle");
        return t;
    }

    public static TextBlock SectionTitle(string text)
    {
        var t = new TextBlock { Text = text };
        t.SetResourceReference(FrameworkElement.StyleProperty, "SectionTitle");
        return t;
    }

    public static TextBlock Glyph(string glyph, double size = 16, string brush = "TextFillColorPrimaryBrush")
    {
        var t = new TextBlock { Text = glyph, FontSize = size, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        t.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    public static Border Card(UIElement child, double padding = 16, double radius = 8)
    {
        var b = new Border { Child = child, Padding = new Thickness(padding), CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(1) };
        b.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        b.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return b;
    }

    /// <summary>Eine Zeile wie SettingsCard: Symbol links, Titel + Beschreibung, Bedienelement rechts</summary>
    public static Border Row(UIElement? icon, string title, string? description, UIElement? right, Action? onClick = null)
    {
        var dock = new DockPanel { MinHeight = 36 };
        if (icon is FrameworkElement fi)
        {
            fi.Margin = new Thickness(0, 0, 16, 0);
            fi.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(fi, Dock.Left);
            dock.Children.Add(fi);
        }
        if (right is FrameworkElement fr)
        {
            fr.Margin = new Thickness(16, 0, 0, 0);
            fr.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(fr, Dock.Right);
            dock.Children.Add(fr);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Text(title, wrap: false));
        if (description is not null) text.Children.Add(Caption(description));
        dock.Children.Add(text);
        var card = Card(dock, radius: 4);
        card.Padding = new Thickness(16, 12, 16, 12);
        card.Margin = new Thickness(0, 0, 0, 4);
        if (onClick is not null)
        {
            card.Cursor = System.Windows.Input.Cursors.Hand;
            card.MouseEnter += (_, _) => card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorSecondaryBrush");
            card.MouseLeave += (_, _) => card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
            card.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase) onClick(); };
        }
        return card;
    }

    public static Button Button(string text, Action onClick, bool accent = false, string? glyph = null, bool danger = false)
    {
        var b = new Button();
        if (glyph is null) b.Content = text;
        else
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
            ((TextBlock)sp.Children[0]).SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            sp.Children.Add(new TextBlock { Text = text });
            b.Content = sp;
        }
        if (accent) b.SetResourceReference(FrameworkElement.StyleProperty, "AccentButtonStyle");
        if (danger) b.SetResourceReference(Control.ForegroundProperty, "SystemFillColorCriticalBrush");
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>Kleiner Symbol-Knopf ohne Fläche (Zeilen in Listen)</summary>
    public static Button IconButton(string glyph, Action onClick, string tip, bool danger = false)
    {
        var b = new Button { Content = Glyph(glyph, 14, danger ? "SystemFillColorCriticalBrush" : "TextFillColorPrimaryBrush"), ToolTip = tip, Width = 36, Height = 32 };
        b.SetResourceReference(FrameworkElement.StyleProperty, "SubtleButton");
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Button Link(string text, Action onClick)
    {
        var b = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left };
        b.SetResourceReference(FrameworkElement.StyleProperty, "LinkButton");
        b.Click += (_, _) => onClick();
        return b;
    }

    /// <summary>Eingabefeld mit grauem Hinweistext, solange es leer ist</summary>
    public static Grid Input(string placeholder, out TextBox box)
    {
        var field = new TextBox();
        var hint = Text(placeholder, brush: "TextFillColorTertiaryBrush", wrap: false);
        hint.IsHitTestVisible = false;
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.Margin = new Thickness(12, 0, 12, 1);
        field.TextChanged += (_, _) => hint.Visibility = field.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box = field;
        return new Grid { Children = { field, hint } };
    }

    public static Grid Password(string placeholder, out PasswordBox box)
    {
        var field = new PasswordBox();
        var hint = Text(placeholder, brush: "TextFillColorTertiaryBrush", wrap: false);
        hint.IsHitTestVisible = false;
        hint.VerticalAlignment = VerticalAlignment.Center;
        hint.Margin = new Thickness(12, 0, 12, 1);
        field.PasswordChanged += (_, _) => hint.Visibility = field.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box = field;
        return new Grid { Children = { field, hint } };
    }

    /// <summary>Feld mit Überschrift darüber (wie TextBox.Header in WinUI)</summary>
    public static StackPanel Labeled(string label, FrameworkElement field)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        var l = Text(label, 14);
        l.Margin = new Thickness(0, 0, 0, 6);
        sp.Children.Add(l);
        sp.Children.Add(field);
        return sp;
    }

    /// <summary>Großes Symbol in einem farbigen, abgerundeten Quadrat (Kopf von Seiten und Karten)</summary>
    public static Border IconTile(string glyph, Color color, double size)
    {
        var b = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.22), Background = Palette.Gradient(color),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(1),
        };
        var t = Glyph(glyph, size * 0.44, "TextOnAccentFillColorPrimaryBrush");
        t.Foreground = Brushes.White;
        b.Child = t;
        return b;
    }

    private static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2);
        var s = string.Concat(parts.Select(p => char.ToUpperInvariant(p[0])));
        return s.Length == 0 ? "?" : s;
    }

    /// <summary>Rundes Bild mit den Initialen, Farbe pro Name (wie PersonPicture)</summary>
    public static Border Avatar(string name, double size)
    {
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = Palette.Gradient(Palette.For(name)),
            Child = new TextBlock
            {
                Text = Initials(name), Foreground = Brushes.White, FontSize = size * 0.38, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    /// <summary>Platzhalter, wenn niemand angemeldet ist</summary>
    public static Border EmptyAvatar(double size)
    {
        var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Child = Glyph("", size * 0.45, "TextFillColorSecondaryBrush") };
        b.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
        return b;
    }

    /// <summary>Clan-Zeichen: abgerundetes Quadrat mit dem ersten Buchstaben</summary>
    public static Border ClanIcon(string name, string key, double size)
    {
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.26), Background = Palette.Gradient(Palette.For(key)),
            Child = new TextBlock
            {
                Text = name.Length == 0 ? "?" : char.ToUpperInvariant(name[0]).ToString(), Foreground = Brushes.White,
                FontSize = size * 0.52, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    /// <summary>Kleine Kennung (Rolle, Status)</summary>
    public static Border Badge(string text, bool accent = false)
    {
        var t = Text(text, 11, FontWeights.SemiBold, accent ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush", wrap: false);
        var b = new Border { Child = t, CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 2), VerticalAlignment = VerticalAlignment.Center };
        b.SetResourceReference(Border.BackgroundProperty, accent ? "SystemFillColorAttentionBackgroundBrush" : "ControlFillColorSecondaryBrush");
        b.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        b.BorderThickness = new Thickness(1);
        return b;
    }

    /// <summary>Grüner/grauer Punkt (online, Lobby offen)</summary>
    public static System.Windows.Shapes.Ellipse Dot(bool on, double size = 8)
    {
        var e = new System.Windows.Shapes.Ellipse { Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
        e.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, on ? "SystemFillColorSuccessBrush" : "ControlStrongFillColorDisabledBrush");
        return e;
    }

    /// <summary>Seite mit Rand und Scrollen; Inhalt höchstens 1000 px breit (wie die Windows-Einstellungen)</summary>
    public static ScrollViewer Page(UIElement content, double maxWidth = 1000)
    {
        var holder = new Border { Child = content, MaxWidth = maxWidth, Margin = new Thickness(28, 22, 28, 110) };
        return new ScrollViewer
        {
            Content = holder, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
        };
    }
}
