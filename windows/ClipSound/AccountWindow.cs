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
        Closed += (_, _) => _account.Changed -= OnAccountChanged;

        Render();
        _ = _account.RefreshMeAsync(); // Clans frisch laden (z. B. inzwischen freigegeben)
    }

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

        _root.Children.Add(Caption("Clan beitreten (Invite-Code)"));
        var joinBox = new TextBox();
        _root.Children.Add(joinBox);
        _root.Children.Add(Btn("Beitreten", async (_, _) =>
        {
            if (joinBox.Text.Trim().Length < 4) return;
            var st = await _account.JoinClanAsync(joinBox.Text.Trim());
            note.Text = st switch { "member" => "Du bist jetzt im Clan.", "requested" => "Anfrage gestellt – ein Admin muss dich freigeben.", _ => _account.Problem ?? "Fehlgeschlagen." };
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

/// <summary>Eine Clan-Seite: Session, Anfragen (Admin), Clan-Sounds, versteckter Invite-Code.</summary>
public sealed class ClanWindow : Window
{
    private readonly ClanRoom _room;
    private readonly StackPanel _root = new() { Margin = new Thickness(18) };
    private int _adminTaps;

    public ClanWindow(ClanRoom room)
    {
        _room = room;
        Title = room.Name;
        Width = 480; Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _root };
        _room.Changed += OnChanged;
        Closed += (_, _) => _room.Changed -= OnChanged;
        _ = _room.LoadAsync();
        Render();
    }

    private void OnChanged() => Dispatcher.Invoke(Render);

    private static TextBlock Head(string t) => new() { Text = t, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
    private static TextBlock Muted(string t) => new() { Text = t, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private static Button Btn(string t, RoutedEventHandler h)
    {
        var b = new Button { Content = t, Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        b.Click += h; return b;
    }

    private void Render()
    {
        _root.Children.Clear();
        _root.Children.Add(Head(_room.Name));
        _root.Children.Add(Muted(_room.IsAdmin ? "Rolle: Admin" : "Rolle: Mitglied"));

        _root.Children.Add(Btn(_room.SessionCode is null ? "Clan-Session starten" : $"Session beitreten ({_room.SessionCode})",
            async (_, _) => await _room.StartSessionAsync()));
        _root.Children.Add(Muted("Alle im Clan können der Session beitreten und gemeinsam abspielen. Ist niemand mehr drin, startet die nächste Session neu."));

        // Beitritts-Anfragen (nur Admin)
        if (_room.IsAdmin && _room.Requests.Count > 0)
        {
            _root.Children.Add(new TextBlock { Text = "Beitritts-Anfragen", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 4) });
            foreach (var r in _room.Requests)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var ok = new Button { Content = "Annehmen", Margin = new Thickness(6, 0, 0, 0) };
                var no = new Button { Content = "Ablehnen", Margin = new Thickness(6, 0, 0, 0) };
                var req = r;
                ok.Click += async (_, _) => await _room.ApproveAsync(req.UserId, true);
                no.Click += async (_, _) => await _room.ApproveAsync(req.UserId, false);
                DockPanel.SetDock(ok, Dock.Right); DockPanel.SetDock(no, Dock.Right);
                row.Children.Add(no); row.Children.Add(ok);
                row.Children.Add(new TextBlock { Text = $"{r.Name}  ({r.Email})", VerticalAlignment = VerticalAlignment.Center });
                _root.Children.Add(row);
            }
        }

        // Clan-Sounds
        _root.Children.Add(new TextBlock { Text = $"Clan-Sounds ({_room.Sounds.Count})", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 4) });
        if (_room.Sounds.Count == 0) _root.Children.Add(Muted("Noch keine Sounds im Clan. Teile welche aus deiner Bibliothek."));
        foreach (var s in _room.Sounds)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var snd = s;
            if (_room.IsAdmin)
            {
                var del = new Button { Content = "Löschen", Margin = new Thickness(6, 0, 0, 0), ToolTip = "Aus dem Clan löschen (nur Admins)" };
                del.Click += async (_, _) =>
                {
                    if (MessageBox.Show(this, $"„{snd.Title}“ aus dem Clan löschen? Er verschwindet für alle im Clan.", "Clan-Sound löschen",
                            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
                        await _room.DeleteSoundAsync(snd);
                };
                DockPanel.SetDock(del, Dock.Right); row.Children.Add(del);
            }
            var take = new Button { Content = "In meine Sounds", Margin = new Thickness(6, 0, 0, 0), IsEnabled = s.Available };
            take.Click += async (_, _) => await _room.TakeToSoloAsync(snd);
            DockPanel.SetDock(take, Dock.Right); row.Children.Add(take);
            var title = new TextBlock
            {
                Text = s.Available ? s.Title : $"{s.Title}  (niemand online, der ihn hat)",
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            if (!s.Available) title.Foreground = Brushes.Gray;
            row.Children.Add(title);
            _root.Children.Add(row);
        }
        _root.Children.Add(Btn("Sounds im Clan teilen…", (_, _) =>
        {
            var dlg = new OpenFileDialog
            {
                Multiselect = true, InitialDirectory = _room.LibraryFolder,
                Filter = "Audio|*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.opus;*.flac;*.aif;*.aiff;*.caf;*.webm|Alle Dateien|*.*",
            };
            if (dlg.ShowDialog() == true)
                _ = _room.ShareAsync(dlg.FileNames);
        }));
        _root.Children.Add(Muted("Die Sounds bleiben auf euren Geräten und gehen direkt von einem zum anderen. Der Server speichert keine Dateien."));

        // Admin: Invite-Code versteckt (3× auf „Admin“ klicken)
        if (_room.IsAdmin)
        {
            var adminHead = new TextBlock { Text = "Admin", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 4), Cursor = Cursors.Hand };
            adminHead.MouseLeftButtonDown += (_, e) =>
            {
                _adminTaps += 1;
                if (e.ClickCount >= 3 || _adminTaps >= 3) { _adminTaps = 0; ShowInvite(); }
            };
            _root.Children.Add(adminHead);
            _root.Children.Add(new TextBlock { Text = "3× auf „Admin“ klicken für den Invite-Code.", Foreground = Brushes.Gray, FontSize = 11 });
        }

        if (_room.Note is { } note) _root.Children.Add(Muted(note));
    }

    private void ShowInvite()
    {
        var box = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        box.Children.Add(new TextBlock { Text = "Invite-Code", Foreground = Brushes.Gray, FontSize = 12 });
        box.Children.Add(new TextBox { Text = _room.InviteCode ?? "–", IsReadOnly = true, FontSize = 16 });
        box.Children.Add(Btn("Neuen Code erzeugen", async (_, _) => await _room.RotateInviteAsync()));
        _root.Children.Add(box);
    }
}
