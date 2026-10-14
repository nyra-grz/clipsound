import SwiftUI
import UniformTypeIdentifiers

// MARK: - Gemeinsame Bausteine (karten-basiert, kein Formular)

extension View {
    func clanCard() -> some View {
        padding(16)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 14, style: .continuous).fill(.quinary))
    }
}

struct Avatar: View {
    let name: String
    var size: CGFloat = 40
    private var initials: String {
        let parts = name.split(separator: " ").prefix(2)
        let s = parts.compactMap { $0.first }.map(String.init).joined()
        return s.isEmpty ? "?" : s.uppercased()
    }
    var body: some View {
        Circle().fill(Color.accentColor.gradient)
            .frame(width: size, height: size)
            .overlay(Text(initials).font(.system(size: size * 0.4, weight: .semibold)).foregroundStyle(.white))
    }
}

struct Badge: View {
    let text: String
    var color: Color = .secondary
    var body: some View {
        Text(text.uppercased())
            .font(.caption2.weight(.semibold))
            .padding(.horizontal, 7).padding(.vertical, 3)
            .background(Capsule().fill(color.opacity(0.16)))
            .foregroundStyle(color)
    }
}

// MARK: - Sheet

struct AccountSheet: View {
    @ObservedObject var account: Account
    let lobby: Lobby
    let library: SoundLibrary
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            Group {
                if account.loggedIn { AccountHome(account: account, lobby: lobby, library: library) }
                else { AccountAuth(account: account) }
            }
            .navigationTitle(account.loggedIn ? "Konto & Clans" : "Anmelden")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Fertig") { dismiss() } } }
            .navigationDestination(for: ClanSummary.self) { c in
                ClanPage(account: account, lobby: lobby, library: library, clan: c)
            }
        }
    }
}

// MARK: - Anmelden / Registrieren / Bestätigen

private struct AccountAuth: View {
    @ObservedObject var account: Account
    enum Mode { case login, register, verify }
    @State private var mode: Mode = .login
    @State private var email = ""
    @State private var name = ""
    @State private var password = ""
    @State private var code = ""

    var body: some View {
        ScrollView {
            VStack(spacing: 18) {
                Image(systemName: mode == .verify ? "envelope.badge" : "person.3.sequence.fill")
                    .font(.system(size: 42)).foregroundStyle(.tint).padding(.top, 16)
                Text(mode == .verify ? "E-Mail bestätigen" : "ClipSound-Clans").font(.title2.weight(.bold))
                Text(mode == .verify ? "Gib den Code aus der E-Mail ein." : "Melde dich an, um Clans beizutreten und zusammen abzuspielen.")
                    .foregroundStyle(.secondary).multilineTextAlignment(.center)

                VStack(spacing: 12) {
                    if mode == .verify {
                        Text("Code an \(email)").font(.callout).foregroundStyle(.secondary)
                        TextField("6-stelliger Code", text: $code).textFieldStyle(.roundedBorder).keyboardType(.numberPad).font(.title3)
                        Button {
                            Task { _ = await account.verify(email: email, code: code) }
                        } label: { Text("Bestätigen").frame(maxWidth: .infinity) }
                            .buttonStyle(.borderedProminent).controlSize(.large)
                            .disabled(code.filter(\.isNumber).count < 6 || account.busy)
                        HStack {
                            Button("Code erneut senden") { Task { await account.resend(email: email) } }
                            Spacer()
                            Button("Zurück") { mode = .login; account.problem = nil }
                        }.font(.callout)
                    } else {
                        Picker("", selection: $mode) {
                            Text("Anmelden").tag(Mode.login)
                            Text("Registrieren").tag(Mode.register)
                        }.pickerStyle(.segmented).labelsHidden()

                        TextField("E-Mail", text: $email).textFieldStyle(.roundedBorder)
                            .textContentType(.emailAddress).textInputAutocapitalization(.never).autocorrectionDisabled()
                        if mode == .register { TextField("Name", text: $name).textFieldStyle(.roundedBorder) }
                        SecureField("Passwort", text: $password).textFieldStyle(.roundedBorder)
                        Button {
                            Task {
                                if mode == .login {
                                    let r = await account.login(email: email, password: password)
                                    if r.needsVerify { mode = .verify }
                                } else if await account.register(email: email, name: name, password: password) { mode = .verify }
                            }
                        } label: { Text(mode == .login ? "Anmelden" : "Registrieren").frame(maxWidth: .infinity) }
                            .buttonStyle(.borderedProminent).controlSize(.large)
                            .disabled(account.busy || email.isEmpty || password.isEmpty || (mode == .register && name.count < 2))
                    }
                    if account.busy { ProgressView() }
                    if let p = account.problem {
                        Label(p, systemImage: "exclamationmark.triangle.fill").foregroundStyle(.orange).font(.callout)
                    }
                }
                .clanCard()
            }
            .padding(20)
        }
    }
}

// MARK: - Konto + Clans

private struct AccountHome: View {
    @ObservedObject var account: Account
    let lobby: Lobby
    let library: SoundLibrary
    @State private var newClan = ""
    @State private var joinCode = ""
    @State private var note: String?

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 20) {
                HStack(spacing: 12) {
                    Avatar(name: account.user?.name ?? "?")
                    VStack(alignment: .leading, spacing: 2) {
                        Text(account.user?.name ?? "").font(.headline)
                        Text(account.user?.email ?? "").font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button("Abmelden", role: .destructive) { account.logout() }.font(.callout)
                }
                .clanCard()

                Text("Deine Clans").font(.title3.weight(.semibold))
                if account.clans.isEmpty {
                    Text("Noch in keinem Clan. Leg unten einen an oder tritt mit einem Invite-Code bei.")
                        .foregroundStyle(.secondary).clanCard()
                } else {
                    ForEach(account.clans) { c in
                        NavigationLink(value: c) { ClanRow(clan: c) }.buttonStyle(.plain)
                    }
                }

                VStack(alignment: .leading, spacing: 10) {
                    Label("Neuer Clan", systemImage: "plus.circle.fill").font(.subheadline.weight(.semibold))
                    HStack {
                        TextField("Clan-Name", text: $newClan).textFieldStyle(.roundedBorder)
                        Button("Anlegen") {
                            Task { if let inv = await account.createClan(name: newClan) { note = "Clan angelegt. Invite-Code: \(inv)"; newClan = "" } }
                        }
                        .buttonStyle(.bordered)
                        .disabled(newClan.trimmingCharacters(in: .whitespaces).count < 2 || account.busy)
                    }
                }.clanCard()

                VStack(alignment: .leading, spacing: 10) {
                    Label("Clan beitreten", systemImage: "person.badge.plus").font(.subheadline.weight(.semibold))
                    HStack {
                        TextField("Invite-Code", text: $joinCode).textFieldStyle(.roundedBorder)
                            .textInputAutocapitalization(.characters).autocorrectionDisabled()
                        Button("Beitreten") {
                            Task {
                                if let st = await account.joinClan(code: joinCode) {
                                    note = st == "member" ? "Du bist jetzt im Clan." : "Anfrage gestellt – ein Admin muss dich freigeben."; joinCode = ""
                                }
                            }
                        }
                        .buttonStyle(.bordered)
                        .disabled(joinCode.trimmingCharacters(in: .whitespaces).count < 4 || account.busy)
                    }
                }.clanCard()

                if let note { Text(note).font(.callout).foregroundStyle(.secondary) }
                if let p = account.problem { Label(p, systemImage: "exclamationmark.triangle.fill").foregroundStyle(.orange).font(.callout) }
            }
            .padding(20)
        }
        .task { await account.refreshMe() }
    }
}

struct ClanRow: View {
    let clan: ClanSummary
    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: clan.isAdmin ? "crown.fill" : "person.3.fill")
                .font(.title3).foregroundStyle(clan.isAdmin ? Color.orange : Color.secondary).frame(width: 28)
            VStack(alignment: .leading, spacing: 3) {
                Text(clan.name).font(.body.weight(.semibold))
                Text("\(clan.members) \(clan.members == 1 ? "Mitglied" : "Mitglieder")").font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            Badge(text: clan.isAdmin ? "Admin" : "Mitglied", color: clan.isAdmin ? .orange : .secondary)
            Image(systemName: "chevron.right").font(.caption.weight(.semibold)).foregroundStyle(.tertiary)
        }
        .clanCard()
    }
}

// MARK: - Clan-Seite

struct ClanPage: View {
    @StateObject private var room: ClanRoom
    @State private var importing = false

    init(account: Account, lobby: Lobby, library: SoundLibrary, clan: ClanSummary) {
        _room = StateObject(wrappedValue: ClanRoom(account: account, lobby: lobby, library: library, clanId: clan.id, name: clan.name))
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 18) {
                HStack(spacing: 14) {
                    Image(systemName: room.isAdmin ? "crown.fill" : "person.3.fill")
                        .font(.system(size: 30)).foregroundStyle(room.isAdmin ? Color.orange : Color.secondary)
                    VStack(alignment: .leading, spacing: 4) {
                        Text(room.name).font(.title2.weight(.bold))
                        Badge(text: room.isAdmin ? "Admin" : "Mitglied", color: room.isAdmin ? .orange : .secondary)
                    }
                    Spacer()
                }

                VStack(alignment: .leading, spacing: 10) {
                    Button { Task { await room.startSession() } } label: {
                        Label(room.sessionCode == nil ? "Clan-Session starten" : "Session beitreten (\(room.sessionCode!))",
                              systemImage: "play.circle.fill").frame(maxWidth: .infinity)
                    }
                    .buttonStyle(.borderedProminent).controlSize(.large).disabled(room.busy)
                    if room.lobby.active {
                        Button("Session verlassen", role: .destructive) { room.lobby.leave() }.font(.callout)
                    }
                    Text("Alle im Clan können beitreten und zusammen abspielen. Ist niemand mehr drin, startet die nächste Session neu.")
                        .font(.caption).foregroundStyle(.secondary)
                }.clanCard()

                if room.isAdmin && !room.requests.isEmpty {
                    VStack(alignment: .leading, spacing: 12) {
                        Text("Beitritts-Anfragen").font(.headline)
                        ForEach(room.requests) { r in
                            HStack(spacing: 10) {
                                Avatar(name: r.name, size: 32)
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(r.name).font(.subheadline.weight(.medium))
                                    Text(r.email).font(.caption).foregroundStyle(.secondary)
                                }
                                Spacer()
                                Button("Annehmen") { Task { await room.approve(r.userId, ok: true) } }
                                    .buttonStyle(.borderedProminent).controlSize(.small)
                                Button("Ablehnen") { Task { await room.approve(r.userId, ok: false) } }.controlSize(.small)
                            }
                        }
                    }.clanCard()
                }

                VStack(alignment: .leading, spacing: 10) {
                    HStack {
                        Text("Clan-Sounds").font(.headline)
                        Spacer()
                        Text("\(room.sounds.count)").foregroundStyle(.secondary).monospacedDigit()
                    }
                    if room.sounds.isEmpty { Text("Noch keine Sounds im Clan. Lade welche hoch.").foregroundStyle(.secondary) }
                    ForEach(room.sounds) { s in
                        HStack(spacing: 10) {
                            Image(systemName: "waveform").foregroundStyle(.secondary)
                            Text(s.title)
                            Spacer()
                            Button { Task { await room.takeToSolo(s) } } label: { Image(systemName: "square.and.arrow.down") }
                                .buttonStyle(.borderless)
                        }
                        .padding(.vertical, 2)
                    }
                    Button { importing = true } label: { Label("Sounds in den Clan laden…", systemImage: "square.and.arrow.up") }
                        .disabled(room.busy)
                }.clanCard()

                ClanMembers(room: room)
                if room.isAdmin { ClanAdminInvite(room: room) }

                if room.busy { ProgressView() }
                if let n = room.note { Text(n).font(.callout).foregroundStyle(.secondary) }
            }
            .padding(20)
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

private struct ClanMembers: View {
    @ObservedObject var room: ClanRoom
    @State private var show = false
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Button { withAnimation { show.toggle() } } label: {
                HStack {
                    Text("Mitglieder (\(room.members.count))").font(.headline)
                    Spacer()
                    Image(systemName: show ? "chevron.up" : "chevron.down").foregroundStyle(.secondary)
                }
            }.buttonStyle(.plain)
            if show {
                ForEach(room.members) { m in
                    HStack(spacing: 10) {
                        Avatar(name: m.name, size: 26)
                        Text(m.name)
                        Spacer()
                        if m.role == "admin" { Badge(text: "Admin", color: .orange) }
                    }
                }
            }
        }.clanCard()
    }
}

private struct ClanAdminInvite: View {
    @ObservedObject var room: ClanRoom
    @State private var show = false
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Admin").font(.headline)
                .onTapGesture(count: 3) { withAnimation { show.toggle() } }
            if show {
                HStack {
                    Text("Invite-Code").foregroundStyle(.secondary)
                    Spacer()
                    Text(room.inviteCode ?? "–").font(.title3.monospaced()).textSelection(.enabled)
                }
                Button("Neuen Code erzeugen") { Task { await room.rotateInvite() } }.font(.callout)
            } else {
                Text("3× auf „Admin“ tippen für den Invite-Code.").font(.caption2).foregroundStyle(.secondary)
            }
        }.clanCard()
    }
}
