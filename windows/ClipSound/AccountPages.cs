using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace ClipSound;

/// <summary>Seite „Konto“ (wie am Mac): anmelden/registrieren/bestätigen, sonst Profil, Clans, Clan anlegen oder beitreten.</summary>
public sealed class AccountPage : ContentControl
{
    private readonly Account _account;
    private readonly MainWindow _shell;

    private enum Screen { None, Auth, Home }
    private Screen _screen;

    public AccountPage(Account account, MainWindow shell)
    {
        _account = account; _shell = shell;
        Focusable = false;
    }

    /// <summary>Beim Öffnen der Seite und wenn sich das Konto ändert. Eingabefelder nicht mitten im Tippen neu bauen.</summary>
    public void Refresh()
    {
        var want = _account.LoggedIn ? Screen.Home : Screen.Auth;
        if (want != _screen) { _screen = want; Render(); }
        else if (_screen == Screen.Home) FillClans();
    }

    private void Render()
    {
        if (_screen == Screen.Home) BuildHome();
        else BuildAuth();
    }

    // ---------- Anmelden / Registrieren / Bestätigen ----------
    private int _mode;          // 0 = Anmelden, 1 = Registrieren, 2 = Bestätigen
    private string _verifyEmail = "";

    private void BuildAuth()
    {
        var root = new StackPanel { MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Center };
        var hero = Ui.IconTile(_mode == 2 ? "" : "", Palette.Orange, 76);
        hero.HorizontalAlignment = HorizontalAlignment.Center;
        hero.Margin = new Thickness(0, 14, 0, 18);
        root.Children.Add(hero);
        var title = Ui.Text(_mode == 2 ? "E-Mail bestätigen" : "ClipSound-Clans", 32, FontWeights.SemiBold);
        title.SetResourceReference(TextBlock.FontFamilyProperty, "DisplayFont");
        title.HorizontalAlignment = HorizontalAlignment.Center;
        root.Children.Add(title);
        var lead = Ui.Secondary(_mode == 2 ? $"Wir haben einen 6-stelligen Code an {_verifyEmail} geschickt."
                                           : "Melde dich an, um Clans beizutreten und mit Freunden zusammen abzuspielen.", 16);
        lead.TextAlignment = TextAlignment.Center;
        lead.Margin = new Thickness(0, 6, 0, 24);
        root.Children.Add(lead);

        var form = new StackPanel();
        var status = Ui.Text("", 13, brush: "SystemFillColorCautionBrush");
        status.Margin = new Thickness(0, 12, 0, 0);
        status.Visibility = Visibility.Collapsed;
        void Problem(string text) { status.Text = text; status.Visibility = Visibility.Visible; }

        if (_mode == 2)
        {
            form.Children.Add(Ui.Labeled("Code", Ui.Input("123456", out var codeBox)));
            codeBox.FontSize = 18;
            codeBox.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            async void Verify()
            {
                if (await _account.VerifyAsync(_verifyEmail, codeBox.Text.Trim())) return; // Wechsel kommt über Changed
                Problem(_account.Problem ?? "Code falsch.");
            }
            var ok = Ui.Button("Bestätigen", Verify, accent: true);
            ok.HorizontalAlignment = HorizontalAlignment.Stretch;
            form.Children.Add(ok);
            codeBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Verify(); e.Handled = true; } };
            var links = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
            var back = Ui.Link("Zurück", () => { _mode = 0; _account.Problem = null; Render(); });
            DockPanel.SetDock(back, Dock.Right);
            links.Children.Add(back);
            links.Children.Add(Ui.Link("Code erneut senden", async () => { await _account.ResendAsync(_verifyEmail); _shell.ShowToast("Neuer Code verschickt."); }));
            form.Children.Add(links);
            form.Children.Add(status);
            Dispatcher.BeginInvoke(() => codeBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
        }
        else
        {
            // Reiter wie SelectorBar
            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-12, -4, 0, 18) };
            foreach (var (label, mode) in new[] { ("Anmelden", 0), ("Registrieren", 1) })
            {
                var tab = new RadioButton { Content = label, IsChecked = _mode == mode, GroupName = "authmode" };
                tab.SetResourceReference(FrameworkElement.StyleProperty, "SelectorItem");
                int m = mode;
                tab.Checked += (_, _) => { if (_mode != m) { _mode = m; _account.Problem = null; Render(); } };
                tabs.Children.Add(tab);
            }
            form.Children.Add(tabs);

            form.Children.Add(Ui.Labeled("E-Mail", Ui.Input("name@beispiel.de", out var emailBox)));
            TextBox? nameBox = null;
            if (_mode == 1) form.Children.Add(Ui.Labeled("Name", Ui.Input("So sehen dich die anderen", out nameBox)));
            form.Children.Add(Ui.Labeled("Passwort", Ui.Password(_mode == 1 ? "Mindestens 8 Zeichen" : "Passwort", out var pwBox)));

            async void Submit()
            {
                var email = emailBox.Text.Trim();
                if (email.Length == 0 || pwBox.Password.Length == 0) return;
                if (_mode == 0)
                {
                    var r = await _account.LoginAsync(email, pwBox.Password);
                    if (r.ok) return;
                    if (r.needsVerify) { _verifyEmail = email; _mode = 2; Render(); return; }
                    Problem(_account.Problem ?? "Anmeldung fehlgeschlagen.");
                }
                else if (await _account.RegisterAsync(email, nameBox?.Text.Trim() ?? "", pwBox.Password)) { _verifyEmail = email; _mode = 2; Render(); }
                else Problem(_account.Problem ?? "Registrierung fehlgeschlagen.");
            }
            void EnterSubmits(Control c) => c.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Submit(); e.Handled = true; } };
            EnterSubmits(emailBox); EnterSubmits(pwBox);
            if (nameBox is not null) EnterSubmits(nameBox);

            var go = Ui.Button(_mode == 0 ? "Anmelden" : "Registrieren", Submit, accent: true);
            go.HorizontalAlignment = HorizontalAlignment.Stretch;
            go.Margin = new Thickness(0, 6, 0, 0);
            form.Children.Add(go);
            form.Children.Add(status);
        }
        root.Children.Add(Ui.Card(form, 24));

        var privacy = Ui.Caption("Mit einem Konto gilt die Datenschutzerklärung. Deine Sounds bleiben auf deinen Geräten.");
        privacy.TextAlignment = TextAlignment.Center;
        privacy.Margin = new Thickness(0, 18, 0, 4);
        root.Children.Add(privacy);
        var link = Ui.Link("Datenschutzerklärung", () => Relay.OpenInBrowser(Account.PrivacyUrl));
        link.HorizontalAlignment = HorizontalAlignment.Center;
        root.Children.Add(link);

        Content = Ui.Page(root);
    }

    // ---------- Konto + Clans ----------
    private readonly StackPanel _clanList = new();

    private void BuildHome()
    {
        var root = new StackPanel();
        root.Children.Add(Ui.PageTitle("Konto"));
        var sub = Ui.Secondary("Konto & Clans");
        sub.Margin = new Thickness(1, 0, 0, 18);
        root.Children.Add(sub);

        // Profil
        if (_account.User is { } u)
        {
            var profile = new DockPanel();
            var avatar = Ui.Avatar(u.Name, 60);
            avatar.Margin = new Thickness(0, 0, 18, 0);
            profile.Children.Add(avatar);
            var logout = Ui.Button("Abmelden", () => _account.Logout());
            logout.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(logout, Dock.Right);
            profile.Children.Add(logout);
            var who = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            who.Children.Add(Ui.Text(u.Name, 20, FontWeights.SemiBold, wrap: false));
            who.Children.Add(Ui.Secondary(u.Email));
            profile.Children.Add(who);
            DockPanel.SetDock(avatar, Dock.Left);
            root.Children.Add(Ui.Card(profile, 20));
        }

        root.Children.Add(Ui.SectionTitle("Deine Clans"));
        root.Children.Add(_clanList);
        FillClans();

        root.Children.Add(Ui.SectionTitle("Clan hinzufügen"));
        var note = Ui.Caption("");
        note.Margin = new Thickness(2, 4, 0, 0);
        void Note(string text) => note.Text = text;

        var newRow = new StackPanel { Orientation = Orientation.Horizontal };
        newRow.Children.Add(new Border { Width = 220, Child = Ui.Input("Clan-Name", out var newClan), Margin = new Thickness(0, 0, 8, 0) });
        async void Create()
        {
            if (newClan.Text.Trim().Length < 2) return;
            var inv = await _account.CreateClanAsync(newClan.Text.Trim());
            if (inv is not null) { Note($"Clan angelegt. Invite-Code zum Weitergeben: {inv}"); newClan.Text = ""; }
            else Note(_account.Problem ?? "Fehlgeschlagen.");
        }
        newRow.Children.Add(Ui.Button("Anlegen", Create));
        newClan.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Create(); e.Handled = true; } };
        root.Children.Add(Ui.Row(Ui.Glyph(""), "Neuer Clan", "Leg einen Clan an und lade Freunde ein.", newRow));

        var joinRow = new StackPanel { Orientation = Orientation.Horizontal };
        joinRow.Children.Add(new Border { Width = 220, Child = Ui.Input("Invite-Code oder Link", out var joinBox), Margin = new Thickness(0, 0, 8, 0) });
        async void Join()
        {
            if (joinBox.Text.Trim().Length < 4) return;
            var st = await _account.JoinClanAsync(joinBox.Text.Trim());
            Note(st is not null ? "Du bist jetzt im Clan." : _account.Problem ?? "Fehlgeschlagen.");
            if (st is not null) joinBox.Text = "";
        }
        joinRow.Children.Add(Ui.Button("Beitreten", Join));
        joinBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Join(); e.Handled = true; } };
        root.Children.Add(Ui.Row(Ui.Glyph(""), "Clan beitreten", "Wer einen Code oder Link hat, ist sofort drin.", joinRow));
        root.Children.Add(note);

        // Datenschutz: Erklärung und Konto löschen (DSGVO Art. 17)
        root.Children.Add(Ui.SectionTitle("Datenschutz"));
        root.Children.Add(Ui.Row(Ui.Glyph(""), "Datenschutzerklärung", "Deine Sounds bleiben auf deinen Geräten.",
            Ui.Glyph("", 14, "TextFillColorSecondaryBrush"), () => Relay.OpenInBrowser(Account.PrivacyUrl)));
        var delete = Ui.Button("Konto löschen …", DeleteAccount, danger: true);
        root.Children.Add(Ui.Row(Ui.Glyph("", 16, "SystemFillColorCriticalBrush"), "Konto löschen",
            "Löscht E-Mail, Name, Passwort und Mitgliedschaften.", delete));

        Content = Ui.Page(root);
    }

    private async void DeleteAccount()
    {
        var pw = await _shell.AskPasswordAsync("Konto wirklich löschen?",
            "E-Mail, Name, Passwort und Mitgliedschaften werden gelöscht. Clans, die dir gehören, übernimmt ein anderes Mitglied; bist du allein drin, wird der Clan aufgelöst. Das kann man nicht rückgängig machen.",
            "Endgültig löschen");
        if (pw is null) return;
        if (pw.Length == 0) { _shell.ShowToast("Bitte dein Passwort eingeben."); return; }
        if (!await _account.DeleteAccountAsync(pw)) _shell.ShowError(_account.Problem ?? "Löschen fehlgeschlagen.");
    }

    /// <summary>Clan-Liste – wird neu gefüllt, wenn sich das Konto ändert (angelegt, beigetreten, freigegeben)</summary>
    private void FillClans()
    {
        _clanList.Children.Clear();
        if (_account.Clans.Count == 0)
        {
            var empty = Ui.Secondary("Noch in keinem Clan. Leg unten einen an oder tritt mit einem Invite-Code bei.");
            empty.Margin = new Thickness(2, 0, 0, 4);
            _clanList.Children.Add(empty);
        }
        foreach (var c in _account.Clans)
        {
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            right.Children.Add(Ui.Badge(c.IsAdmin ? "Admin" : "Mitglied", c.IsAdmin));
            var chevron = Ui.Glyph("", 12, "TextFillColorSecondaryBrush");
            chevron.Margin = new Thickness(16, 0, 0, 0);
            right.Children.Add(chevron);
            var clan = c;
            _clanList.Children.Add(Ui.Row(Ui.ClanIcon(c.Name, c.Id, 32), c.Name,
                $"{c.Members} {(c.Members == 1 ? "Mitglied" : "Mitglieder")}", right, () => _shell.OpenClan(clan.Id)));
        }
    }
}

/// <summary>Clan-Seite wie ein Discord-Server: Session, Einladen, Clan-Sounds, Mitglieder (wer online ist).
/// Lädt sich von selbst neu, wenn der Server meldet, dass sich im Clan etwas geändert hat.</summary>
public sealed class ClanPage : ContentControl
{
    private readonly ClanRoom _room;
    private readonly Lobby _lobby;
    private readonly MainWindow _shell;
    private readonly System.Windows.Threading.DispatcherTimer _reload;
    private ScrollViewer? _scroll;

    public string ClanId => _room.ClanId;

    public ClanPage(ClanRoom room, Lobby lobby, MainWindow shell)
    {
        _room = room; _lobby = lobby; _shell = shell;
        Focusable = false;
        _reload = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _reload.Tick += (_, _) => { _reload.Stop(); _ = _room.LoadAsync(); };
        _room.Changed += OnChanged;
        _room.Account.Share.ClanChanged += OnClanChanged;
        _ = _room.LoadAsync();
        Render();
    }

    /// <summary>Seite verlassen: keine Meldungen mehr</summary>
    public void Detach()
    {
        _room.Changed -= OnChanged;
        _room.Account.Share.ClanChanged -= OnClanChanged;
        _reload.Stop();
    }

    private void OnChanged() => Dispatcher.BeginInvoke(() => { if (_room.Gone) _shell.ClanGone(); else Render(); });

    private void OnClanChanged(string clanId)
    {
        if (clanId != _room.ClanId) return;
        Dispatcher.BeginInvoke(() => { _reload.Stop(); _reload.Start(); }); // mehrere Meldungen zusammenfassen
    }

    /// <summary>Lobby-Wechsel (Session gestartet/verlassen): Knopf neu beschriften</summary>
    public void LobbyChanged() => Render();

    private void Render()
    {
        double scroll = _scroll?.VerticalOffset ?? 0;
        var root = new StackPanel();
        root.Children.Add(Ui.PageTitle(_room.Name));
        var sub = Ui.Secondary("Clan");
        sub.Margin = new Thickness(1, 0, 0, 18);
        root.Children.Add(sub);

        // Kopf + Session
        var head = new DockPanel();
        var icon = Ui.ClanIcon(_room.Name, _room.ClanId, 60);
        icon.Margin = new Thickness(0, 0, 18, 0);
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        var session = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        session.Children.Add(Ui.Button(_room.SessionCode is null ? "Session starten" : "Session beitreten",
            async () => await _room.StartSessionAsync(), accent: true, glyph: ""));
        if (_lobby.Active)
        {
            var leave = Ui.Link("Session verlassen", () => _lobby.Leave());
            leave.SetResourceReference(Control.ForegroundProperty, "SystemFillColorCriticalBrush");
            leave.HorizontalAlignment = HorizontalAlignment.Right;
            leave.Margin = new Thickness(0, 8, 2, 0);
            session.Children.Add(leave);
        }
        DockPanel.SetDock(session, Dock.Right);
        head.Children.Add(session);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(Ui.Text(_room.Name, 20, FontWeights.SemiBold, wrap: false));
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        line.Children.Add(Ui.Badge(_room.Owner ? "Besitzer" : _room.IsAdmin ? "Admin" : "Mitglied", _room.IsAdmin));
        var dot = Ui.Dot(_room.OnlineCount > 0);
        dot.Margin = new Thickness(12, 0, 6, 0);
        line.Children.Add(dot);
        line.Children.Add(Ui.Secondary($"{_room.OnlineCount} von {_room.Members.Count} online"));
        info.Children.Add(line);
        head.Children.Add(info);
        var headStack = new StackPanel { Children = { head } };
        var inSession = _room.Members.Where(m => m.InSession).Select(m => m.Name).ToList();
        var foot = Ui.Caption(inSession.Count == 0 ? "Alle im Clan können beitreten und zusammen abspielen."
                                                   : "Gerade in der Session: " + string.Join(", ", inSession));
        foot.Margin = new Thickness(0, 14, 0, 0);
        headStack.Children.Add(foot);
        root.Children.Add(Ui.Card(headStack, 20));

        // Einladen (wie Discord: mit Code oder Link sofort drin)
        root.Children.Add(Ui.SectionTitle("Freunde einladen"));
        var inviteButtons = new StackPanel { Orientation = Orientation.Horizontal };
        if (_room.IsAdmin)
        {
            var rotate = Ui.Button("Neuer Code", async () =>
            {
                if (await _shell.AskAsync("Neuen Invite-Code erzeugen?", "Der alte Code und Link gelten dann nicht mehr.", "Neuer Code"))
                    await _room.RotateInviteAsync();
            });
            rotate.Margin = new Thickness(0, 0, 8, 0);
            inviteButtons.Children.Add(rotate);
        }
        var copy = Ui.Button("Link kopieren", () =>
        {
            if (_room.InviteLink is not { } link) return;
            try { Clipboard.SetText(link); _shell.ShowToast("Link kopiert: " + link); } catch { }
        }, glyph: "");
        copy.IsEnabled = _room.InviteLink is not null;
        inviteButtons.Children.Add(copy);
        var codeRow = Ui.Row(Ui.Glyph(""), _room.InviteCode ?? "–", "Wer den Code oder Link hat, ist mit „Clan beitreten“ sofort drin.", inviteButtons);
        if (((DockPanel)codeRow.Child).Children.OfType<StackPanel>().LastOrDefault()?.Children[0] is TextBlock codeText)
        {
            codeText.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            codeText.FontSize = 18;
            codeText.FontWeight = FontWeights.SemiBold;
        }
        root.Children.Add(codeRow);

        // Clan-Sounds
        root.Children.Add(Ui.SectionTitle($"Clan-Sounds ({_room.Sounds.Count})"));
        if (_room.Sounds.Count == 0)
        {
            var empty = Ui.Secondary("Noch keine Sounds im Clan. Teile welche aus deiner Bibliothek.");
            empty.Margin = new Thickness(2, 0, 0, 8);
            root.Children.Add(empty);
        }
        foreach (var s in _room.Sounds)
        {
            var snd = s;
            var square = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(6), Child = Ui.Glyph("", 14) };
            ((TextBlock)square.Child).Foreground = System.Windows.Media.Brushes.White;
            if (s.Available) square.Background = Palette.Gradient(Palette.For(s.Name));
            else square.SetResourceReference(Border.BackgroundProperty, "ControlStrongFillColorDisabledBrush");
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var take = Ui.IconButton("", async () => await _room.TakeToSoloAsync(snd), "In deine eigenen Sounds übernehmen");
            take.IsEnabled = s.Available;
            buttons.Children.Add(take);
            if (_room.IsAdmin)
                buttons.Children.Add(Ui.IconButton("", async () =>
                {
                    if (await _shell.AskAsync($"„{snd.Title}“ aus dem Clan löschen?",
                            "Der Sound verschwindet für alle im Clan. Wer ihn schon in seine eigenen Sounds übernommen hat, behält ihn.", "Löschen", danger: true))
                        await _room.DeleteSoundAsync(snd);
                }, "Aus dem Clan löschen (nur Admins)", danger: true));
            var sub2 = s.Available ? (s.By is { } by ? $"von {by}" : "verfügbar")
                                   : "gerade niemand online, der ihn hat" + (s.By is { } b2 ? $" · von {b2}" : "");
            root.Children.Add(Ui.Row(square, s.Title, sub2, buttons));
        }
        root.Children.Add(Ui.Row(Ui.Glyph(""), "Sounds im Clan teilen …", "Sie bleiben auf deinem PC und gehen direkt an die anderen.",
            Ui.Button("Auswählen …", Share)));
        var serverNote = Ui.Caption("Der Server speichert keine Dateien.");
        serverNote.Margin = new Thickness(2, 2, 0, 0);
        root.Children.Add(serverNote);

        // Mitglieder
        root.Children.Add(Ui.SectionTitle($"Mitglieder ({_room.Members.Count})"));
        foreach (var m in _room.Members)
        {
            var who = m;
            var avatar = new Grid { Width = 32, Height = 32 };
            avatar.Children.Add(Ui.Avatar(m.Name, 32));
            var presence = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(2),
                                        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -2, -2) };
            presence.SetResourceReference(Border.BorderBrushProperty, "SolidBackgroundFillColorBaseBrush");
            presence.SetResourceReference(Border.BackgroundProperty, m.Online ? "SystemFillColorSuccessBrush" : "ControlStrongFillColorDisabledBrush");
            avatar.Children.Add(presence);
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            if (m.Owner || m.IsAdmin) right.Children.Add(Ui.Badge(m.Owner ? "Besitzer" : "Admin", true));
            if (_room.IsAdmin && !m.Owner && m.Id != _room.MyId)
            {
                var kick = Ui.IconButton("", async () =>
                {
                    if (await _shell.AskAsync($"{who.Name} aus dem Clan entfernen?",
                            "Mit dem Invite-Code kommt die Person wieder rein. Erzeug danach einen neuen Code, wenn sie draußen bleiben soll.", "Entfernen", danger: true))
                        await _room.KickAsync(who);
                }, "Aus dem Clan entfernen", danger: true);
                kick.Margin = new Thickness(8, 0, 0, 0);
                right.Children.Add(kick);
            }
            root.Children.Add(Ui.Row(avatar, m.Name, m.InSession ? "in der Session" : m.Online ? "online" : "offline", right));
        }

        var leaveClan = Ui.Button(_room.Owner ? "Clan auflösen …" : "Clan verlassen …", async () =>
        {
            bool ok = await _shell.AskAsync(_room.Owner ? $"Clan „{_room.Name}“ auflösen?" : $"Clan „{_room.Name}“ verlassen?",
                _room.Owner ? "Der Clan ist dann für alle weg, mit allen Clan-Sounds. Eigene Sounds bleiben auf den Geräten."
                            : "Mit dem Invite-Code kannst du wieder beitreten.",
                _room.Owner ? "Auflösen" : "Verlassen", danger: true);
            if (ok) await _room.LeaveOrDissolveAsync();
        }, danger: true);
        leaveClan.HorizontalAlignment = HorizontalAlignment.Left;
        leaveClan.Margin = new Thickness(0, 24, 0, 0);
        root.Children.Add(leaveClan);

        if (_room.Note is { } note)
        {
            var n = Ui.Caption(note);
            n.Margin = new Thickness(2, 10, 0, 0);
            root.Children.Add(n);
        }

        _scroll = Ui.Page(root);
        Content = _scroll;
        // Neu aufgebaut (z. B. jemand kommt online): an derselben Stelle bleiben
        if (scroll > 0) _scroll.Loaded += (_, _) => _scroll.ScrollToVerticalOffset(scroll);
    }

    private void Share()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Sounds zum Teilen auswählen – sie bleiben auf deinem PC",
            Multiselect = true, InitialDirectory = _room.LibraryFolder,
            Filter = "Audio|*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.opus;*.flac;*.aif;*.aiff;*.caf;*.webm|Alle Dateien|*.*",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true) _ = _room.ShareAsync(dlg.FileNames);
    }
}
