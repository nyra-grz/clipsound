using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ClipSound;

public partial class SettingsWindow : Window
{
    private readonly Settings _settings;
    private readonly Action _iconChanged;

    public SettingsWindow(Settings settings, Action iconChanged)
    {
        InitializeComponent();
        _settings = settings;
        _iconChanged = iconChanged;
        KeepInTray.IsChecked = settings.KeepInTray;
        UpdateHint();
        UpdateIcon();
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) SetIcon(files[0]);
        };
    }

    private void KeepInTray_Changed(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return; // kommt schon während InitializeComponent
        _settings.KeepInTray = KeepInTray.IsChecked == true;
        _settings.Save();
        UpdateHint();
    }

    private void UpdateHint() => KeepInTrayHint.Text = _settings.KeepInTray
        ? "Das X schließt nur das Fenster. Globale Tastenkürzel (mit Alt oder Strg) gehen weiter, ClipSound bleibt unten rechts im Infobereich. Beenden: Rechtsklick auf das Symbol → Beenden."
        : "Das X beendet ClipSound. Danach gehen auch die Tastenkürzel nicht mehr.";

    private void UpdateIcon()
    {
        var custom = CustomIcon.Load();
        IconPreview.Source = custom ?? new BitmapImage(new Uri("pack://application:,,,/AppIcon.ico"));
        IconState.Text = custom is null ? "Standard-Icon" : "Eigenes Icon";
        ResetIcon.Visibility = custom is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetIcon(string path)
    {
        if (!CustomIcon.Set(path))
        {
            MessageBox.Show(this, "Das ist kein Bild, das ClipSound lesen kann (PNG, JPG, BMP, GIF, ICO).", "ClipSound", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        UpdateIcon();
        _iconChanged();
    }

    private void ChooseIcon_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Bild für das ClipSound-Icon", Filter = "Bilder|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico" };
        if (dialog.ShowDialog(this) == true) SetIcon(dialog.FileName);
    }

    private void ResetIcon_Click(object sender, RoutedEventArgs e)
    {
        CustomIcon.Reset();
        UpdateIcon();
        _iconChanged();
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();
}
