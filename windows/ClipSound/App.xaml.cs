using System.Threading;
using System.Windows;

namespace ClipSound;

public partial class App : Application
{
    // Nur eine ClipSound-Instanz: eine zweite würde die globalen Tastenkürzel nicht bekommen
    // und ein zweites Symbol in den Infobereich setzen. Ein zweiter Start holt stattdessen das Fenster nach vorne.
    private const string InstanceName = @"Local\ClipSound.Instanz";
    private const string ShowSignalName = @"Local\ClipSound.Zeigen";
    private static Mutex? _instance;
    private static EventWaitHandle? _showSignal;

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

        // Testläufe dürfen mehrere Instanzen haben
        if (!ClipSound.MainWindow.TestMode && !TakeInstance(e.Args))
        {
            try { EventWaitHandle.OpenExisting(ShowSignalName).Set(); } catch { /* die andere beendet sich gerade */ }
            Shutdown();
            return;
        }

        var window = new ClipSound.MainWindow();
        MainWindow = window;
        window.Show();
        if (_showSignal is not null) ListenForSecondStart(window);
    }

    private static bool TakeInstance(string[] args)
    {
        _instance = new Mutex(true, InstanceName, out bool first);
        if (first) { _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName); return true; }
        // Nach einem Update startet die neue Version, während sich die alte noch beendet – kurz warten
        if (args.Contains("--updated"))
        {
            try { first = _instance.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { first = true; } // alte Instanz ist weg, ohne freizugeben
            if (first) { _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName); return true; }
        }
        return false;
    }

    /// <summary>Ein zweiter Start meldet sich über das Signal – dann das Fenster zeigen (auch aus dem Infobereich).</summary>
    private static void ListenForSecondStart(ClipSound.MainWindow window)
    {
        var signal = _showSignal!;
        var thread = new Thread(() =>
        {
            while (true)
            {
                try { signal.WaitOne(); } catch { return; }
                window.Dispatcher.BeginInvoke(window.ShowFromTray);
            }
        }) { IsBackground = true, Name = "ClipSound zweiter Start" };
        thread.Start();
    }
}
