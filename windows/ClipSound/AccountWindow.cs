using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace ClipSound;

/// <summary>Konto & Clans für Windows – Anmelden/Registrieren, Clan-Liste, Clan-Seite (programmatisch aufgebaut).</summary>
public sealed class AccountWindow : Window
{
    private readonly Account _account;
    private readonly Lobby _lobby;
    private readonly SoundLibrary _library;
    private readonly StackPanel _root = new() { Margin = new Thickness(18) };

    private enum Screen { Auth, Home }
    private Screen _screen = Screen.Auth;

    public AccountWindow(Account account, Lobby lobby, SoundLibrary library)
    {
        _account = account; _lobby = lobby; _library = library;
        Title = "Konto & Clans";
        Width = 480; Height = 660;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _root };

        _account.Changed += OnAccountChanged;
        _account.Share.ClanChanged += OnClanChanged;
        Closed += (_, _) => { _account.Changed -= OnAccountChanged; _account.Share.ClanChanged -= OnClanChanged; };

        Render();
        _ = _account.RefreshMeAsync(); // Clans frisch laden (z. B. inzwischen freigegeben)
    }

    // Live-Update vom Server (beigetreten, entfernt, aufgelöst …): Clan-Liste neu laden
    private void OnClanChanged(string clanId) => Dispatcher.BeginInvoke(() => _ = _account.RefreshMeAsync());

    private void OnAccountChanged() => Dispatcher.BeginInvoke(() =>
    {
        // Zwischen Auth und Home wechseln; sonst nur die Clan-Liste auffrischen –
        // Eingabefelder nicht mitten im Tippen neu bauen.
        var want = _account.LoggedIn ? Screen.Home : Screen.Auth;
        if (want != _screen) { _screen = want; Render(); }
        else if (_screen == Screen.Home) FillClans();
    });

    private void Render()
    {
        _root.Children.Clear();
        if (_account.LoggedIn) BuildHome();
        else BuildAuth();
    }

    // ---------- Bausteine ----------
    private static TextBlock Head(string t) => new() { Text = t, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
    private static TextBlock Caption(string t) => new() { Text = t, Margin = new Thickness(0, 10, 0, 2), Foreground = Brushes.Gray, FontSize = 12 };
    private static TextBlock Muted(string t) => new() { Text = t, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    /// <param name="isDefault">Enter im Eingabefeld drückt diesen Knopf</param>
    private static Button Btn(string t, RoutedEventHandler h, bool isDefault = false)
    {
        var b = new Button { Content = t, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, IsDefault = isDefault };
        if (isDefault) b.SetResourceReference(StyleProperty, "AccentButtonStyle");
        b.Click += h; return b;
    }
    /// <summary>Kleiner Link zur Datenschutzerklärung (öffnet den Browser)</summary>
    public static TextBlock PrivacyLink(string text = "Datenschutzerklärung")
    {
        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(text)) { NavigateUri = Account.PrivacyUrl };
        link.RequestNavigate += (_, _) => Relay.OpenInBrowser(Account.PrivacyUrl);
        return new TextBlock(link) { Margin = new Thickness(0, 14, 0, 0) };
    }

    // ---------- Anmelden / Registrieren / Bestätigen ----------
    private int _mode;          // 0 = Login, 1 = Register, 2 = Verify
    private string _verifyEmail = "";

    private void BuildAuth()
    {
        _screen = Screen.Auth;
        _root.Children.Add(Head(_mode == 2 ? "E-Mail bestätigen" : "Anmelden"));

        var status = new TextBlock { Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };

        if (_mode == 2)
        {
            _root.Children.Add(Muted($"Wir haben einen 6-stelligen Code an {_verifyEmail} geschickt."));
            _root.Children.Add(Caption("Code"));
            var codeBox = new TextBox();
            _root.Children.Add(codeBox);
            _root.Children.Add(Btn("Bestätigen", async (_, _) =>
            {
                if (await _account.VerifyAsync(_verifyEmail, codeBox.Text.Trim())) return; // Wechsel passiert über Changed
                status.Text = _account.Problem ?? "Code falsch.";
            }, isDefault: true));
            _root.Children.Add(Btn("Code erneut senden", async (_, _) => { await _account.ResendAsync(_verifyEmail); status.Foreground = Brushes.Gray; status.Text = "Neuer Code verschickt."; }));
            _root.Children.Add(Btn("Zurück", (_, _) => { _mode = 0; Render(); }));
            _root.Children.Add(status);
            return;
        }

        var toggle = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        var rbLogin = new RadioButton { Content = "Anmelden", IsChecked = _mode == 0, Margin = new Thickness(0, 0, 14, 0), GroupName = "mode" };
        var rbReg = new RadioButton { Content = "Registrieren", IsChecked = _mode == 1, GroupName = "mode" };
        rbLogin.Checked += (_, _) => { if (_mode != 0) { _mode = 0; Render(); } };
        rbReg.Checked += (_, _) => { if (_mode != 1) { _mode = 1; Render(); } };
        toggle.Children.Add(rbLogin); toggle.Children.Add(rbReg);
        _root.Children.Add(toggle);

        _root.Children.Add(Caption("E-Mail"));
        var emailBox = new TextBox();
        _root.Children.Add(emailBox);

        TextBox? nameBox = null;
        if (_mode == 1)
        {
            _root.Children.Add(Caption("Name"));
            nameBox = new TextBox();
            _root.Children.Add(nameBox);
        }

        _root.Children.Add(Caption("Passwort"));
        var pwBox = new PasswordBox();
        _root.Children.Add(pwBox);

        _root.Children.Add(Btn(_mode == 0 ? "Anmelden" : "Registrieren & Code anfordern", async (_, _) =>
        {
            var email = emailBox.Text.Trim();
            if (_mode == 0)
            {
                var r = await _account.LoginAsync(email, pwBox.Password);
                if (r.ok) return;
                if (r.needsVerify) { _verifyEmail = email; _mode = 2; Render(); return; }
                status.Text = _account.Problem ?? "Anmeldung fehlgeschlagen.";
            }
            else
            {
                if (await _account.RegisterAsync(email, nameBox?.Text.Trim() ?? "", pwBox.Password)) { _verifyEmail = email; _mode = 2; Render(); }
                else status.Text = _account.Problem ?? "Registrierung fehlgeschlagen.";
            }
        }, isDefault: true));
        _root.Children.Add(status);
        _root.Children.Add(Muted("Mit einem Konto gilt die Datenschutzerklärung. Deine Sounds bleiben auf deinen Geräten."));
        _root.Children.Add(PrivacyLink());
    }

    // ---------- Konto + Clans ----------
    private void BuildHome()
    {
        _screen = Screen.Home;
        _root.Children.Add(Head("Dein Konto & Clans"));

        if (_account.User is { } u)
        {
            _root.Children.Add(new TextBlock { Text = $"Angemeldet als {u.Name}", FontWeight = FontWeights.SemiBold });
            _root.Children.Add(Muted(u.Email));
        }
        _root.Children.Add(Btn("Abmelden", (_, _) => _account.Logout()));

        _root.Children.Add(new TextBlock { Text = "Deine Clans", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 6) });
        _root.Children.Add(_clanList);
        FillClans();

        _root.Children.Add(Caption("Neuen Clan anlegen"));
        var newClan = new TextBox();
        _root.Children.Add(newClan);
        var note = new TextBlock { Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        _root.Children.Add(Btn("Anlegen", async (_, _) =>
        {
            if (newClan.Text.Trim().Length < 2) return;
            var inv = await _account.CreateClanAsync(newClan.Text.Trim());
            if (inv is not null) { note.Text = $"Clan angelegt. Invite-Code zum Weitergeben: {inv}"; newClan.Text = ""; }
            else note.Text = _account.Problem ?? "Fehlgeschlagen.";
        }));

        _root.Children.Add(Caption("Clan beitreten (Invite-Code oder Link)"));
        var joinBox = new TextBox();
        _root.Children.Add(joinBox);
        _root.Children.Add(Btn("Beitreten", async (_, _) =>
        {
            if (joinBox.Text.Trim().Length < 4) return;
            var st = await _account.JoinClanAsync(joinBox.Text.Trim());
            note.Text = st is not null ? "Du bist jetzt im Clan." : _account.Problem ?? "Fehlgeschlagen.";
            if (st is not null) joinBox.Text = "";
        }));
        _root.Children.Add(note);

        // Datenschutz: Erklärung und Konto löschen (DSGVO Art. 17)
        _root.Children.Add(PrivacyLink());
        _root.Children.Add(new TextBlock { Text = "Konto löschen", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 2) });
        _root.Children.Add(Muted("Löscht E-Mail, Name, Passwort und Mitgliedschaften. Clans, die dir gehören, übernimmt ein anderes Mitglied; bist du allein drin, wird der Clan aufgelöst."));
        _root.Children.Add(Caption("Passwort zur Bestätigung"));
        var delPw = new PasswordBox();
        _root.Children.Add(delPw);
        var delStatus = new TextBlock { Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        var del = Btn("Konto endgültig löschen", async (_, _) =>
        {
            if (delPw.Password.Length == 0) { delStatus.Text = "Bitte dein Passwort eingeben."; return; }
            if (MessageBox.Show(this, "Konto wirklich löschen? Das kann man nicht rückgängig machen.", "Konto löschen",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            if (!await _account.DeleteAccountAsync(delPw.Password)) delStatus.Text = _account.Problem ?? "Löschen fehlgeschlagen.";
        });
        del.Foreground = Brushes.OrangeRed;
        _root.Children.Add(del);
        _root.Children.Add(delStatus);
    }

    /// <summary>Clan-Liste – wird auch neu gefüllt, wenn sich das Konto ändert (angelegt, beigetreten, freigegeben)</summary>
    private readonly StackPanel _clanList = new();

    private void FillClans()
    {
        _clanList.Children.Clear();
        if (_account.Clans.Count == 0)
            _clanList.Children.Add(Muted("Noch in keinem Clan. Leg einen an oder tritt mit einem Invite-Code bei."));
        foreach (var c in _account.Clans)
        {
            var row = new Button { Margin = new Thickness(0, 2, 0, 2), Padding = new Thickness(10, 8, 10, 8), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var rp = new DockPanel();
            var cnt = new TextBlock { Text = $"{c.Members} {(c.Members == 1 ? "Mitglied" : "Mitglieder")}", Foreground = Brushes.Gray };
            DockPanel.SetDock(cnt, Dock.Right); rp.Children.Add(cnt);
            rp.Children.Add(new TextBlock { Text = c.IsAdmin ? "👑 " + c.Name : c.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Content = rp;
            var clan = c;
            row.Click += (_, _) => OpenClan(clan);
            _clanList.Children.Add(row);
        }
    }

    // ---------- Clan-Seite (eigenes Fenster, pro Clan nur eins) ----------
    private readonly Dictionary<string, ClanWindow> _clanWindows = new();

    private void OpenClan(ClanSummary clan)
    {
        if (_clanWindows.TryGetValue(clan.Id, out var open)) { open.Activate(); return; }
        var win = new ClanWindow(new ClanRoom(_account, _lobby, _library, clan.Id, clan.Name)) { Owner = this };
        _clanWindows[clan.Id] = win;
        win.Closed += (_, _) => _clanWindows.Remove(clan.Id);
        win.Show();
    }
}

/// <summary>Eine Clan-Seite wie ein Discord-Server: Session, Einladen, Clan-Sounds, Mitglieder (wer online ist).
/// Lädt sich von selbst neu, wenn der Server meldet, dass sich im Clan etwas geändert hat.</summary>
public sealed class ClanWindow : Window
{
    private readonly ClanRoom _room;
    private readonly StackPanel _root = new() { Margin = new Thickness(18) };
    private readonly System.Windows.Threading.DispatcherTimer _reload;

    public ClanWindow(ClanRoom room)
    {
        _room = room;
        Title = room.Name;
        Width = 500; Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _root };
        _reload = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _reload.Tick += (_, _) => { _reload.Stop(); _ = _room.LoadAsync(); };
        _room.Changed += OnChanged;
        _room.Account.Share.ClanChanged += OnClanChanged;
        Closed += (_, _) => { _room.Changed -= OnChanged; _room.Account.Share.ClanChanged -= OnClanChanged; _reload.Stop(); };
        _ = _room.LoadAsync();
        Render();
    }

    private void OnChanged() => Dispatcher.BeginInvoke(() => { if (_room.Gone) Close(); else Render(); });
    private void OnClanChanged(string clanId)
    {
        if (clanId != _room.ClanId) return;
        Dispatcher.BeginInvoke(() => { _reload.Stop(); _reload.Start(); }); // mehrere Meldungen zusammenfassen
    }

    private static TextBlock Head(string t) => new() { Text = t, FontSize = 20, FontWeight = FontWeights.SemiBold };
    private static TextBlock Section(string t) => new() { Text = t, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 6) };
    private static TextBlock Muted(string t) => new() { Text = t, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private static Button Btn(string t, RoutedEventHandler h, bool accent = false)
    {
        var b = new Button { Content = t, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        if (accent) b.SetResourceReference(StyleProperty, "AccentButtonStyle");
        b.Click += h; return b;
    }
    private static Button Small(string t, RoutedEventHandler h, string? tip = null)
    {
        var b = new Button { Content = t, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(8, 2, 8, 2), ToolTip = tip };
        b.Click += h; return b;
    }

    private bool Ask(string text, string title) =>
        MessageBox.Show(this, text, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void Render()
    {
        _root.Children.Clear();
        Title = _room.Name;
        _root.Children.Add(Head(_room.Name));
        _root.Children.Add(Muted($"{(_room.Owner ? "Besitzer" : _room.IsAdmin ? "Admin" : "Mitglied")} · {_room.OnlineCount} von {_room.Members.Count} online"));

        // Session
        _root.Children.Add(Btn(_room.SessionCode is null ? "Clan-Session starten" : "Session beitreten",
            async (_, _) => await _room.StartSessionAsync(), accent: true));
        var inSession = _room.Members.Where(m => m.InSession).Select(m => m.Name).ToList();
        _root.Children.Add(Muted(inSession.Count == 0 ? "Alle im Clan können beitreten und gemeinsam abspielen."
                                                      : "Gerade in der Session: " + string.Join(", ", inSession)));

        // Einladen (wie Discord: mit Code oder Link sofort drin)
        _root.Children.Add(Section("Freunde einladen"));
        var inviteRow = new DockPanel();
        var copy = Small("Link kopieren", (_, _) =>
        {
            if (_room.InviteLink is { } link) { try { Clipboard.SetText(link); _room.Note = "Link kopiert: " + link; Render(); } catch { } }
        });
        DockPanel.SetDock(copy, Dock.Right); inviteRow.Children.Add(copy);
        inviteRow.Children.Add(new TextBox { Text = _room.InviteCode ?? "–", IsReadOnly = true, FontSize = 18, FontFamily = new FontFamily("Cascadia Mono, Consolas"), BorderThickness = new Thickness(0) });
        _root.Children.Add(inviteRow);
        _root.Children.Add(Muted("Wer den Code oder Link hat, ist mit „Clan beitreten“ sofort drin."));
        if (_room.IsAdmin)
            _root.Children.Add(Btn("Neuen Code erzeugen (alter gilt dann nicht mehr)", async (_, _) => await _room.RotateInviteAsync()));

        // Clan-Sounds
        _root.Children.Add(Section($"Clan-Sounds ({_room.Sounds.Count})"));
        if (_room.Sounds.Count == 0) _root.Children.Add(Muted("Noch keine Sounds im Clan. Teile welche aus deiner Bibliothek."));
        foreach (var s in _room.Sounds)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var snd = s;
            if (_room.IsAdmin)
            {
                var del = Small("Löschen", async (_, _) =>
                {
                    if (Ask($"„{snd.Title}“ aus dem Clan löschen? Er verschwindet für alle im Clan.", "Clan-Sound löschen")) await _room.DeleteSoundAsync(snd);
                }, "Aus dem Clan löschen (nur Admins)");
                DockPanel.SetDock(del, Dock.Right); row.Children.Add(del);
            }
            var take = Small("In meine Sounds", async (_, _) => await _room.TakeToSoloAsync(snd));
            take.IsEnabled = s.Available;
            DockPanel.SetDock(take, Dock.Right); row.Children.Add(take);
            var text = new StackPanel();
            var title = new TextBlock { Text = s.Title, TextTrimming = TextTrimming.CharacterEllipsis };
            if (!s.Available) title.Foreground = Brushes.Gray;
            text.Children.Add(title);
            text.Children.Add(new TextBlock
            {
                Text = s.Available ? (s.By is { } by ? $"von {by}" : "verfügbar") : "gerade niemand online, der ihn hat" + (s.By is { } b2 ? $" · von {b2}" : ""),
                Foreground = Brushes.Gray, FontSize = 11,
            });
            row.Children.Add(text);
            _root.Children.Add(row);
        }
        _root.Children.Add(Btn("Sounds im Clan teilen…", (_, _) =>
        {
            var dlg = new OpenFileDialog
            {
                Multiselect = true, InitialDirectory = _room.LibraryFolder,
                Filter = "Audio|*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.opus;*.flac;*.aif;*.aiff;*.caf;*.webm|Alle Dateien|*.*",
            };
            if (dlg.ShowDialog() == true) _ = _room.ShareAsync(dlg.FileNames);
        }));
        _root.Children.Add(Muted("Die Sounds bleiben auf euren Geräten und gehen direkt von einem zum anderen. Der Server speichert keine Dateien."));

        // Mitglieder
        _root.Children.Add(Section($"Mitglieder ({_room.Members.Count})"));
        foreach (var m in _room.Members)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var who = m;
            if (_room.IsAdmin && !m.Owner && m.Id != _room.MyId)
            {
                var kick = Small("Entfernen", async (_, _) =>
                {
                    if (Ask($"{who.Name} aus dem Clan entfernen?\n\nMit dem Invite-Code kommt die Person wieder rein – erzeug danach einen neuen Code, wenn sie draußen bleiben soll.", "Entfernen"))
                        await _room.KickAsync(who);
                });
                DockPanel.SetDock(kick, Dock.Right); row.Children.Add(kick);
            }
            if (m.Owner || m.IsAdmin)
            {
                var badge = new TextBlock { Text = m.Owner ? "Besitzer" : "Admin", Foreground = Brushes.DarkOrange, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(badge, Dock.Right); row.Children.Add(badge);
            }
            var dot = new TextBlock { Text = "●", Foreground = m.Online ? Brushes.LimeGreen : Brushes.Gray, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(dot, Dock.Left); row.Children.Add(dot);
            var name = new StackPanel();
            var nameText = new TextBlock { Text = m.Name };
            if (!m.Online) nameText.Foreground = Brushes.Gray;
            name.Children.Add(nameText);
            name.Children.Add(new TextBlock { Text = m.InSession ? "in der Session" : m.Online ? "online" : "offline", Foreground = Brushes.Gray, FontSize = 11 });
            row.Children.Add(name);
            _root.Children.Add(row);
        }

        var leave = Btn(_room.Owner ? "Clan auflösen …" : "Clan verlassen …", async (_, _) =>
        {
            var q = _room.Owner ? $"Clan „{_room.Name}“ auflösen? Er ist dann für alle weg, mit allen Clan-Sounds."
                                : $"Clan „{_room.Name}“ verlassen? Mit dem Invite-Code kannst du wieder beitreten.";
            if (Ask(q, _room.Owner ? "Clan auflösen" : "Clan verlassen")) await _room.LeaveOrDissolveAsync();
        });
        leave.Foreground = Brushes.OrangeRed;
        leave.Margin = new Thickness(0, 20, 0, 0);
        _root.Children.Add(leave);

        if (_room.Note is { } note) _root.Children.Add(Muted(note));
    }
}
