using System.Windows;

namespace ClipSound;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Unerwartete Fehler anzeigen statt kommentarlos abzustürzen
        DispatcherUnhandledException += (_, args) =>
        {
            if (ClipSound.MainWindow.TestMode)
            {
                // Testlauf: Fehler in eine Datei schreiben und beenden statt ein Fenster zu zeigen
                System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "clipsound-crash.txt"), args.Exception.ToString());
                args.Handled = true;
                Shutdown(1);
                return;
            }
            MessageBox.Show(args.Exception.Message, "ClipSound – Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
