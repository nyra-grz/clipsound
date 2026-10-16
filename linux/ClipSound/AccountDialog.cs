namespace ClipSound;

/// <summary>
/// Konto & Clans als Dialog mit Seiten (Adw.NavigationView): Anmelden/Registrieren, deine Clans,
/// Clan-Seite wie ein Discord-Server (Session, Einladen, Clan-Sounds, Mitglieder). Aktualisiert sich live.
/// </summary>
public sealed class AccountDialog
{
    private readonly Account _account;
    private readonly Lobby _lobby;
    private readonly SoundLibrary _library;
    private readonly Adw.Dialog _dialog = Adw.Dialog.New();
    private readonly Adw.NavigationView _nav = Adw.NavigationView.New();
    private readonly Adw.ToastOverlay _toasts = Adw.ToastOverlay.New();
    private readonly Adw.NavigationPage _root;
    private bool _home;
    private int _mode;                 // 0 = Anmelden, 1 = Registrieren, 2 = Code bestätigen
    private string _email = "";
    private Adw.PreferencesGroup? _clanGroup;
    private readonly List<Gtk.Widget> _clanRows = new();
    private ClanPage? _openClan;

    public AccountDialog(Account account, Lobby lobby, SoundLibrary library)
    {
        _account = account; _lobby = lobby; _library = library;
        _dialog.SetTitle("Konto & Clans");
        _dialog.SetContentWidth(560);
        _dialog.SetContentHeight(680);
        _root = Adw.NavigationPage.New(Gtk.Box.New(Gtk.Orientation.Vertical, 0), "Konto & Clans");
        _root.SetTag("start");
        _nav.Add(_root);
        _toasts.SetChild(_nav);
        _dialog.SetChild(_toasts);
        Render();
        _account.Changed += OnAccountChanged;
        _account.Share.ClanChanged += OnClanChanged;
        _dialog.OnClosed += (_, _) => { _account.Changed -= OnAccountChanged; _account.Share.ClanChanged -= OnClanChanged; };
        _ = _account.RefreshMeAsync(); // Clans frisch laden
    }

    public void Present(Gtk.Widget parent) => _dialog.Present(parent);

    private void Toast(string text) => _toasts.AddToast(Adw.Toast.New(GLib.Markup.EscapeText(text)));

    private void OnClanChanged(string clanId) => Ui.OnMain(() =>
    {
        _ = _account.RefreshMeAsync();
        if (_openClan?.Room.ClanId == clanId) _openClan.ReloadSoon();
    });

    private bool _openedFirst;

    private void OnAccountChanged() => Ui.OnMain(() =>
    {
        // Test: --open-clan öffnet gleich die erste Clan-Seite (für Screenshots)
        if (!_openedFirst && Environment.GetCommandLineArgs().Contains("--open-clan") && _account.Clans.Count > 0)
        {
            _openedFirst = true;
            if (_account.LoggedIn != _home) Render();
            OpenClan(_account.Clans[0]);
            return;
        }
        // Zwischen Anmelden und Konto wechseln; sonst nur die Clan-Liste auffrischen (Eingabefelder nicht neu bauen)
        if (_account.LoggedIn != _home) Render();
        else if (_home) FillClans();
    });

    private void Render()
    {
        _home = _account.LoggedIn;
        _openClan = null;
        var content = _home ? BuildHome() : BuildAuth();
        // Start-Seite bleibt, nur ihr Inhalt wechselt; offene Clan-Seiten schließen
        _nav.PopToPage(_root);
        _root.SetChild(Ui.Page(content));
    }

    // ---------- Anmelden / Registrieren / Bestätigen ----------

    private Gtk.Widget BuildAuth()
    {
        var problem = Ui.Label("", "error");
        problem.SetVisible(false);
        void Problem(string? text) { problem.SetText(text ?? ""); problem.SetVisible(!string.IsNullOrEmpty(text)); }

        var status = Adw.StatusPage.New();
        status.SetIconName(_mode == 2 ? "mail-unread-symbolic" : "system-users-symbolic");
        status.SetTitle(_mode == 2 ? "E-Mail bestätigen" : "ClipSound-Clans");
        status.SetDescription(_mode == 2 ? $"Wir haben einen 6-stelligen Code an {_email} geschickt." : "Melde dich an, um Clans beizutreten und zusammen abzuspielen.");
        status.AddCssClass("compact");

        if (_mode == 2)
        {
            var code = Adw.EntryRow.New();
            code.SetTitle("Code");
            code.SetInputPurpose(Gtk.InputPurpose.Digits);
            async void Verify()
            {
                if (await _account.VerifyAsync(_email, code.GetText().Trim())) return; // Wechsel kommt über Changed
                Problem(_account.Problem ?? "Code falsch.");
            }
            code.OnEntryActivated += (_, _) => Verify();
            var verify = Ui.Button("Bestätigen", Verify, "pill", "suggested-action");
            var resend = Ui.Button("Code erneut senden", async () => { await _account.ResendAsync(_email); Toast("Neuer Code verschickt"); }, "flat");
            var back = Ui.Button("Zurück", () => { _mode = 0; Render(); }, "flat");
            var buttons = Ui.VBox(6, verify, resend, back);
            buttons.SetHalign(Gtk.Align.Center);
            return Ui.VBox(18, status, Ui.Group(null, null, code), buttons, problem).Margins(18);
        }

        // Anmelden/Registrieren als verbundene Umschalter
        var login = Gtk.ToggleButton.NewWithLabel("Anmelden");
        var register = Gtk.ToggleButton.NewWithLabel("Registrieren");
        register.SetGroup(login);
        (_mode == 0 ? login : register).SetActive(true);
        login.OnToggled += (_, _) => { if (login.GetActive() && _mode != 0) { _mode = 0; Render(); } };
        register.OnToggled += (_, _) => { if (register.GetActive() && _mode != 1) { _mode = 1; Render(); } };
        var switcher = Ui.HBox(0, login, register);
        switcher.AddCssClass("linked");
        switcher.SetHalign(Gtk.Align.Center);

        var email = Adw.EntryRow.New();
        email.SetTitle("E-Mail");
        email.SetInputPurpose(Gtk.InputPurpose.Email);
        var name = Adw.EntryRow.New();
        name.SetTitle("Name");
        var password = Adw.PasswordEntryRow.New();
        password.SetTitle("Passwort");
        var fields = Ui.Group(null, null, email);
        if (_mode == 1) fields.Add(name);
        fields.Add(password);

        async void Submit()
        {
            var mail = email.GetText().Trim();
            if (_mode == 0)
            {
                var r = await _account.LoginAsync(mail, password.GetText());
                if (r.ok) return;
                if (r.needsVerify) { _email = mail; _mode = 2; Render(); return; }
                Problem(_account.Problem ?? "Anmeldung fehlgeschlagen.");
            }
            else if (await _account.RegisterAsync(mail, name.GetText().Trim(), password.GetText())) { _email = mail; _mode = 2; Render(); }
            else Problem(_account.Problem ?? "Registrierung fehlgeschlagen.");
        }
        password.OnEntryActivated += (_, _) => Submit();
        var submit = Ui.Button(_mode == 0 ? "Anmelden" : "Registrieren", Submit, "pill", "suggested-action");
        submit.SetHalign(Gtk.Align.Center);
        var privacy = Ui.Row("Datenschutzerklärung", "Deine Sounds bleiben auf deinen Geräten.", () => Open.Url(Account.PrivacyUrl.ToString()));
        privacy.AddSuffix(Gtk.Image.NewFromIconName("adw-external-link-symbolic"));
        return Ui.VBox(18, status, switcher, fields, submit, problem, Ui.Group(null, null, privacy)).Margins(18);
    }

    // ---------- Konto + Clans ----------

    private Gtk.Widget BuildHome()
    {
        var me = _account.User;
        var profile = Ui.Row(me?.Name ?? "", me?.Email);
        profile.AddPrefix(Adw.Avatar.New(40, me?.Name ?? "?", true));
        var logout = Ui.Button("Abmelden", () => _account.Logout(), "flat");
        logout.SetValign(Gtk.Align.Center);
        profile.AddSuffix(logout);

        _clanGroup = Ui.Group("Deine Clans");
        _clanRows.Clear();
        FillClans();

        var create = Adw.EntryRow.New();
        create.SetTitle("Neuen Clan anlegen – Name");
        create.SetShowApplyButton(true);
        create.OnApply += async (_, _) =>
        {
            var n = create.GetText().Trim();
            if (n.Length < 2) return;
            var inv = await _account.CreateClanAsync(n);
            if (inv is not null) { create.SetText(""); Toast($"Clan angelegt – Invite-Code {inv}"); }
            else Toast(_account.Problem ?? "Fehlgeschlagen.");
        };
        var join = Adw.EntryRow.New();
        join.SetTitle("Clan beitreten – Invite-Code oder Link");
        join.SetShowApplyButton(true);
        join.OnApply += async (_, _) =>
        {
            var c = join.GetText().Trim();
            if (c.Length < 4) return;
            var st = await _account.JoinClanAsync(c);
            if (st is not null) { join.SetText(""); Toast("Du bist jetzt im Clan"); }
            else Toast(_account.Problem ?? "Fehlgeschlagen.");
        };

        var privacy = Ui.Row("Datenschutzerklärung", null, () => Open.Url(Account.PrivacyUrl.ToString()));
        privacy.AddSuffix(Gtk.Image.NewFromIconName("adw-external-link-symbolic"));
        var delete = Ui.Row("Konto löschen", "E-Mail, Name, Passwort und Mitgliedschaften werden gelöscht. Eigene Clans übernimmt ein anderes Mitglied.", async () =>
        {
            var pw = await Ui.Prompt(_dialog, "Konto wirklich löschen?", "Zur Sicherheit dein Passwort. Das kann man nicht rückgängig machen.", "Endgültig löschen", password: true, destructive: true);
            if (pw is null) return;
            if (!await _account.DeleteAccountAsync(pw)) Toast(_account.Problem ?? "Löschen fehlgeschlagen.");
        });
        delete.AddCssClass("error");

        return Ui.VBox(24,
            Ui.Group(null, null, profile),
            _clanGroup,
            Ui.Group("Clan anlegen oder beitreten", "Mit einem Invite-Code bist du sofort drin – wie bei Discord.", create, join),
            Ui.Group("Datenschutz", null, privacy, delete)).Margins(18);
    }

    private void FillClans()
    {
        if (_clanGroup is null) return;
        foreach (var r in _clanRows) _clanGroup.Remove(r);
        _clanRows.Clear();
        if (_account.Clans.Count == 0)
        {
            var empty = Ui.Row("Noch in keinem Clan", "Leg einen an oder tritt mit einem Invite-Code bei.");
            _clanGroup.Add(empty);
            _clanRows.Add(empty);
        }
        foreach (var c in _account.Clans)
        {
            var clan = c;
            var row = Ui.Row(c.Name, $"{(c.IsAdmin ? "Admin · " : "")}{c.Members} {(c.Members == 1 ? "Mitglied" : "Mitglieder")}", () => OpenClan(clan));
            row.AddPrefix(Adw.Avatar.New(36, c.Name, true));
            row.AddSuffix(Gtk.Image.NewFromIconName("go-next-symbolic"));
            _clanGroup.Add(row);
            _clanRows.Add(row);
        }
    }

    private void OpenClan(ClanSummary clan)
    {
        _openClan = new ClanPage(new ClanRoom(_account, _lobby, _library, clan.Id, clan.Name), _dialog, Toast, () => { _nav.Pop(); _openClan = null; });
        _nav.Push(_openClan.Page);
    }
}

/// <summary>Eine Clan-Seite wie ein Discord-Server: Session, Einladen, Clan-Sounds, Mitglieder (wer online ist).</summary>
public sealed class ClanPage
{
    public ClanRoom Room { get; }
    public Adw.NavigationPage Page { get; }
    private readonly Gtk.Widget _parent;
    private readonly Action<string> _toast;
    private readonly Action _gone;
    private readonly Gtk.Box _body = Gtk.Box.New(Gtk.Orientation.Vertical, 24);
    private uint _reload;

    public ClanPage(ClanRoom room, Gtk.Widget parent, Action<string> toast, Action gone)
    {
        Room = room; _parent = parent; _toast = toast; _gone = gone;
        _body.Margins(18);
        Page = Adw.NavigationPage.New(Ui.Page(_body), room.Name);
        room.Changed += () => Ui.OnMain(() => { if (Room.Gone) _gone(); else Render(); });
        Render();
        _ = room.LoadAsync();
    }

    /// <summary>Live-Update vom Server: mehrere Meldungen kurz sammeln, dann neu laden</summary>
    public void ReloadSoon()
    {
        if (_reload != 0) return;
        _reload = Ui.Every(TimeSpan.FromMilliseconds(250), () => { _reload = 0; _ = Room.LoadAsync(); return false; });
    }

    private void Render()
    {
        while (_body.GetFirstChild() is { } c) _body.Remove(c);
        Page.SetTitle(Room.Name);
        var r = Room;

        // Kopf + Session
        var status = Adw.StatusPage.New();
        status.SetIconName(r.Owner || r.IsAdmin ? "starred-symbolic" : "system-users-symbolic");
        status.SetTitle(r.Name);
        status.SetDescription($"{(r.Owner ? "Besitzer" : r.IsAdmin ? "Admin" : "Mitglied")} · {r.OnlineCount} von {r.Members.Count} online");
        status.AddCssClass("compact");
        var session = Ui.Button(r.SessionCode is null ? "Clan-Session starten" : "Session beitreten", () => _ = r.StartSessionAsync(), "pill", "suggested-action");
        session.SetHalign(Gtk.Align.Center);
        var inSession = r.Members.Where(m => m.InSession).Select(m => m.Name).ToList();
        var sessionHint = Ui.Label(inSession.Count == 0 ? "Alle im Clan können beitreten und gemeinsam abspielen." : "Gerade in der Session: " + string.Join(", ", inSession), "dim-label");
        sessionHint.SetXalign(0.5f);
        sessionHint.SetJustify(Gtk.Justification.Center);
        status.SetChild(Ui.VBox(12, session, sessionHint));
        _body.Append(status);

        // Einladen
        var invite = Ui.Row("Invite-Code", r.InviteCode ?? "–");
        invite.AddCssClass("property"); // Code groß, Beschriftung klein
        invite.SetSubtitleSelectable(true);
        var copy = Ui.IconButton("edit-copy-symbolic", "Link kopieren", () =>
        {
            if (r.InviteLink is not { } link) return;
            _parent.GetClipboard().SetText(link);
            _toast("Einladungslink kopiert");
        }, "flat");
        copy.SetValign(Gtk.Align.Center);
        invite.AddSuffix(copy);
        var inviteGroup = Ui.Group("Freunde einladen", "Wer den Code oder Link hat, ist mit „Clan beitreten“ sofort drin.", invite);
        if (r.IsAdmin)
        {
            var rotate = Ui.Row("Neuen Code erzeugen", "Der alte Code gilt dann nicht mehr.", () => _ = r.RotateInviteAsync());
            rotate.AddSuffix(Gtk.Image.NewFromIconName("view-refresh-symbolic"));
            inviteGroup.Add(rotate);
        }
        _body.Append(inviteGroup);

        // Clan-Sounds
        var sounds = Ui.Group($"Clan-Sounds ({r.Sounds.Count})", "Die Sounds bleiben auf euren Geräten und gehen direkt von einem zum anderen. Der Server speichert keine Dateien.");
        var share = Ui.Button("Teilen …", () => _ = ShareAsync(), "flat");
        share.SetValign(Gtk.Align.Center);
        sounds.SetHeaderSuffix(share);
        if (r.Sounds.Count == 0) sounds.Add(Ui.Row("Noch keine Sounds im Clan", "Teile welche aus deiner Bibliothek."));
        foreach (var s in r.Sounds)
        {
            var snd = s;
            var row = Ui.Row(s.Title, s.Available ? (s.By is { } by ? $"von {by}" : "verfügbar") : "gerade niemand online, der ihn hat" + (s.By is { } b2 ? $" · von {b2}" : ""));
            var icon = Gtk.Image.NewFromIconName("audio-x-generic-symbolic");
            if (!s.Available) icon.AddCssClass("dim-label");
            row.AddPrefix(icon);
            var take = Ui.IconButton("folder-download-symbolic", "In meine Sounds", () => _ = r.TakeToSoloAsync(snd), "flat");
            take.SetValign(Gtk.Align.Center);
            take.SetSensitive(s.Available);
            row.AddSuffix(take);
            if (r.IsAdmin)
            {
                var del = Ui.IconButton("user-trash-symbolic", "Aus dem Clan löschen (nur Admins)", async () =>
                {
                    if (await Ui.Ask(_parent, $"„{snd.Title}“ aus dem Clan löschen?", "Der Sound verschwindet für alle im Clan. Wer ihn schon übernommen hat, behält ihn.", "Löschen", destructive: true))
                        await r.DeleteSoundAsync(snd);
                }, "flat");
                del.SetValign(Gtk.Align.Center);
                row.AddSuffix(del);
            }
            sounds.Add(row);
        }
        _body.Append(sounds);

        // Mitglieder
        var members = Ui.Group($"Mitglieder ({r.Members.Count})");
        foreach (var m in r.Members)
        {
            var who = m;
            var row = Ui.Row(m.Name, m.InSession ? "in der Session" : m.Online ? "online" : "offline");
            var avatar = Adw.Avatar.New(36, m.Name, true);
            var dot = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
            dot.AddCssClass(m.Online ? "online-dot" : "offline-dot");
            dot.SetHalign(Gtk.Align.End);
            dot.SetValign(Gtk.Align.End);
            var withDot = Gtk.Overlay.New();
            withDot.SetChild(avatar);
            withDot.AddOverlay(dot);
            row.AddPrefix(withDot);
            if (m.Owner || m.IsAdmin)
            {
                var badge = Ui.Label(m.Owner ? "Besitzer" : "Admin", "caption", "accent");
                badge.SetValign(Gtk.Align.Center);
                row.AddSuffix(badge);
            }
            if (r.IsAdmin && !m.Owner && m.Id != r.MyId)
            {
                var kick = Ui.IconButton("list-remove-symbolic", "Aus dem Clan entfernen", async () =>
                {
                    if (await Ui.Ask(_parent, $"{who.Name} entfernen?", "Mit dem Invite-Code kommt die Person wieder rein – erzeug danach einen neuen Code, wenn sie draußen bleiben soll.", "Entfernen", destructive: true))
                        await r.KickAsync(who);
                }, "flat");
                kick.SetValign(Gtk.Align.Center);
                row.AddSuffix(kick);
            }
            members.Add(row);
        }
        _body.Append(members);

        var leave = Ui.Button(r.Owner ? "Clan auflösen …" : "Clan verlassen …", async () =>
        {
            var ok = await Ui.Ask(_parent, r.Owner ? $"„{r.Name}“ auflösen?" : $"„{r.Name}“ verlassen?",
                r.Owner ? "Der Clan ist dann für alle weg, mit allen Clan-Sounds." : "Mit dem Invite-Code kannst du wieder beitreten.",
                r.Owner ? "Auflösen" : "Verlassen", destructive: true);
            if (ok) await r.LeaveOrDissolveAsync();
        }, "pill", "destructive-action");
        leave.SetHalign(Gtk.Align.Center);
        _body.Append(leave);
        if (r.Note is { } note) _toast(note);
        r.Note = null;
    }

    private async Task ShareAsync()
    {
        var dialog = Gtk.FileDialog.New();
        dialog.SetTitle("Sounds zum Teilen auswählen – sie bleiben auf deinem PC");
        dialog.SetInitialFolder(Gio.FileHelper.NewForPath(Room.LibraryFolder));
        try
        {
            var paths = Ui.Paths(await dialog.OpenMultipleAsync(_parent.GetRoot() as Gtk.Window));
            if (paths.Count > 0) await Room.ShareAsync(paths);
        }
        catch { /* abgebrochen */ }
    }
}
