import SwiftUI
import UniformTypeIdentifiers

/// Konto & Clans fürs iPhone. Nicht eingeloggt → Anmelden/Registrieren/Bestätigen;
/// eingeloggt → Clan-Liste; Clan antippen → Clan-Seite.
struct AccountSheet: View {
    @ObservedObject var account: Account
    let lobby: Lobby
    let library: SoundLibrary
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            Group {
                if account.loggedIn {
                    AccountHome(account: account, lobby: lobby, library: library)
                } else {
                    AccountAuth(account: account)
                }
            }
            .navigationTitle(account.loggedIn ? "Konto & Clans" : "Anmelden")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .confirmationAction) { Button("Fertig") { dismiss() } }
            }
            .navigationDestination(for: ClanSummary.self) { c in
                ClanPage(account: account, lobby: lobby, library: library, clan: c)
            }
        }
    }
}

private struct AccountAuth: View {
    @ObservedObject var account: Account
    enum Mode { case login, register, verify }
    @State private var mode: Mode = .login
    @State private var email = ""
    @State private var name = ""
    @State private var password = ""
    @State private var code = ""

    var body: some View {
        Form {
            if mode != .verify {
                Picker("", selection: $mode) {
                    Text("Anmelden").tag(Mode.login)
                    Text("Registrieren").tag(Mode.register)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
            }

            if mode == .verify {
                Section("E-Mail bestätigen") {
                    Text("Wir haben einen 6-stelligen Code an \(email) geschickt.")
                        .font(.callout).foregroundStyle(.secondary)
                    TextField("Code", text: $code)
                        .keyboardType(.numberPad)
                    Button("Bestätigen") { Task { _ = await account.verify(email: email, code: code) } }
                        .disabled(code.filter(\.isNumber).count < 6 || account.busy)
                    Button("Code erneut senden") { Task { await account.resend(email: email) } }
                    Button("Zurück") { mode = .login; account.problem = nil }
                }
            } else {
                Section {
                    TextField("E-Mail", text: $email)
                        .textContentType(.emailAddress)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                    if mode == .register {
                        TextField("Name", text: $name)
                    }
                    SecureField("Passwort", text: $password)
                }
                Section {
                    Button(mode == .login ? "Anmelden" : "Registrieren & Code anfordern") {
                        Task {
                            if mode == .login {
                                let r = await account.login(email: email, password: password)
                                if r.needsVerify { mode = .verify }
                            } else if await account.register(email: email, name: name, password: password) {
                                mode = .verify
                            }
                        }
                    }
                    .disabled(account.busy || email.isEmpty || password.isEmpty || (mode == .register && name.count < 2))
                }
            }

            if account.busy { Section { ProgressView() } }
            if let p = account.problem {
                Section { Label(p, systemImage: "exclamationmark.triangle.fill").foregroundStyle(.orange) }
            }
        }
    }
}

private struct AccountHome: View {
    @ObservedObject var account: Account
    let lobby: Lobby
    let library: SoundLibrary
    @State private var newClan = ""
    @State private var joinCode = ""
    @State private var note: String?

    var body: some View {
        Form {
            Section {
                if let u = account.user {
                    LabeledContent("Angemeldet als", value: u.name)
                    LabeledContent("E-Mail", value: u.email)
                }
                Button("Abmelden", role: .destructive) { account.logout() }
            }

            Section("Deine Clans") {
                if account.clans.isEmpty {
                    Text("Noch in keinem Clan. Leg einen an oder tritt mit einem Invite-Code bei.")
                        .foregroundStyle(.secondary)
                }
                ForEach(account.clans) { c in
                    NavigationLink(value: c) {
                        HStack(spacing: 8) {
                            Image(systemName: c.isAdmin ? "crown.fill" : "person.3.fill")
                                .foregroundStyle(c.isAdmin ? Color.orange : Color.secondary)
                            Text(c.name)
                            Spacer()
                            Text("\(c.members)").font(.caption).foregroundStyle(.secondary)
                        }
                    }
                }
            }

            Section("Neuen Clan anlegen") {
                TextField("Clan-Name", text: $newClan)
                Button("Anlegen") {
                    Task {
                        if let inv = await account.createClan(name: newClan) {
                            note = "Clan angelegt. Invite-Code: \(inv)"; newClan = ""
                        }
                    }
                }
                .disabled(newClan.trimmingCharacters(in: .whitespaces).count < 2 || account.busy)
            }

            Section("Clan beitreten") {
                TextField("Invite-Code", text: $joinCode)
                    .textInputAutocapitalization(.characters)
                    .autocorrectionDisabled()
                Button("Beitreten") {
                    Task {
                        if let st = await account.joinClan(code: joinCode) {
                            note = st == "member" ? "Du bist jetzt im Clan." : "Anfrage gestellt – ein Admin muss dich freigeben."
                            joinCode = ""
                        }
                    }
                }
                .disabled(joinCode.trimmingCharacters(in: .whitespaces).count < 4 || account.busy)
            }

            if let n = note { Section { Text(n).foregroundStyle(.secondary) } }
            if let p = account.problem {
                Section { Label(p, systemImage: "exclamationmark.triangle.fill").foregroundStyle(.orange) }
            }
        }
        .task { await account.refreshMe() }
    }
}

struct ClanPage: View {
    @StateObject private var room: ClanRoom
    @State private var importing = false

    init(account: Account, lobby: Lobby, library: SoundLibrary, clan: ClanSummary) {
        _room = StateObject(wrappedValue: ClanRoom(account: account, lobby: lobby, library: library, clanId: clan.id, name: clan.name))
    }

    var body: some View {
        Form {
            Section {
                LabeledContent("Rolle", value: room.isAdmin ? "Admin" : "Mitglied")
                Button {
                    Task { await room.startSession() }
                } label: {
                    Label(room.sessionCode == nil ? "Clan-Session starten" : "Session beitreten (\(room.sessionCode!))",
                          systemImage: "play.circle.fill")
                }
                .disabled(room.busy)
                if room.lobby.active {
                    Button("Session verlassen", role: .destructive) { room.lobby.leave() }
                }
            } footer: {
                Text("Alle im Clan können der Session beitreten und gemeinsam abspielen. Ist niemand mehr drin, startet die nächste Session neu.")
            }

            if room.isAdmin && !room.requests.isEmpty {
                Section("Beitritts-Anfragen") {
                    ForEach(room.requests) { r in
                        VStack(alignment: .leading, spacing: 6) {
                            Text(r.name)
                            Text(r.email).font(.caption).foregroundStyle(.secondary)
                            HStack {
                                Button("Annehmen") { Task { await room.approve(r.userId, ok: true) } }
                                    .buttonStyle(.borderedProminent).controlSize(.small)
                                Button("Ablehnen") { Task { await room.approve(r.userId, ok: false) } }
                                    .controlSize(.small)
                            }
                        }
                    }
                }
            }

            Section("Clan-Sounds (\(room.sounds.count))") {
                if room.sounds.isEmpty {
                    Text("Noch keine Sounds im Clan. Lade welche hoch.").foregroundStyle(.secondary)
                }
                ForEach(room.sounds) { s in
                    HStack {
                        Image(systemName: "waveform").foregroundStyle(.secondary)
                        Text(s.title)
                        Spacer()
                        Button { Task { await room.takeToSolo(s) } } label: {
                            Image(systemName: "square.and.arrow.down")
                        }
                        .buttonStyle(.borderless)
                    }
                }
                Button {
                    importing = true
                } label: {
                    Label("Sounds in den Clan laden…", systemImage: "square.and.arrow.up")
                }
                .disabled(room.busy)
            }

            ClanMembers(room: room)
            if room.isAdmin { ClanAdminInvite(room: room) }

            if room.busy { Section { ProgressView() } }
            if let n = room.note { Section { Text(n).foregroundStyle(.secondary) } }
        }
        .navigationTitle(room.name)
        .navigationBarTitleDisplayMode(.inline)
        .fileImporter(isPresented: $importing, allowedContentTypes: [.audio], allowsMultipleSelection: true) { result in
            guard case .success(let urls) = result else { return }
            var temps: [Sound] = []
            for u in urls {
                let scoped = u.startAccessingSecurityScopedResource()
                defer { if scoped { u.stopAccessingSecurityScopedResource() } }
                let tmp = FileManager.default.temporaryDirectory.appendingPathComponent(u.lastPathComponent)
                try? FileManager.default.removeItem(at: tmp)
                if (try? FileManager.default.copyItem(at: u, to: tmp)) != nil { temps.append(Sound(url: tmp)) }
            }
            Task { await room.upload(temps) }
        }
        .task { await room.load() }
    }
}

/// Mitglieder eingeklappt.
private struct ClanMembers: View {
    @ObservedObject var room: ClanRoom
    @State private var show = false
    var body: some View {
        Section {
            if show {
                ForEach(room.members) { m in
                    Label(m.name, systemImage: m.role == "admin" ? "crown" : "person")
                }
            }
            Button(show ? "Mitglieder ausblenden" : "Mitglieder zeigen (\(room.members.count))") { show.toggle() }
                .font(.caption)
        }
    }
}

/// Invite-Code versteckt – 3× auf „Admin“ tippen (wie die System-Lautstärke).
private struct ClanAdminInvite: View {
    @ObservedObject var room: ClanRoom
    @State private var show = false
    var body: some View {
        Section {
            if show {
                LabeledContent("Invite-Code", value: room.inviteCode ?? "–")
                Button("Neuen Code erzeugen") { Task { await room.rotateInvite() } }
            }
        } header: {
            Text("Admin")
                .onTapGesture(count: 3) { withAnimation { show.toggle() } }
        } footer: {
            if !show { Text("3× auf „Admin“ tippen für den Invite-Code.").font(.caption2) }
        }
    }
}
