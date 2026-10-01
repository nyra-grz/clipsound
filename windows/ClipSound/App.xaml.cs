using System.Windows;

namespace ClipSound;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Unerwartete Fehler anzeigen statt kommentarlos abzustürzen
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "ClipSound – Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
