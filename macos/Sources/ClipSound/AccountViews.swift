import SwiftUI
import AppKit

// MARK: - Gemeinsame Bausteine (übersichtlich, karten-basiert statt Formular)

extension View {
    /// Weicher Karten-Hintergrund.
    func clanCard() -> some View {
        padding(16)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(.quinary))
            .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(.separator.opacity(0.5)))
    }
}

/// Rundes Monogramm aus den Initialen.
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

/// Kleine farbige Kapsel (Rolle/Status).
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

// MARK: - Fenster

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
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button("Fertig") { dismiss() } } }
            .navigationDestination(for: ClanSummary.self) { c in
                ClanPage(account: account, lobby: lobby, library: library, clan: c)
            }
        }
        .frame(width: 540, height: 660)
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
                    .font(.system(size: 42)).foregroundStyle(.tint)
                    .padding(.top, 24)
                Text(mode == .verify ? "E-Mail bestätigen" : "ClipSound-Clans")
                    .font(.title.weight(.bold))
                Text(mode == .verify ? "Gib den Code aus der E-Mail ein." : "Melde dich an, um Clans beizutreten und zusammen abzuspielen.")
                    .foregroundStyle(.secondary).multilineTextAlignment(.center)

                VStack(spacing: 12) {
                    if mode == .verify {
                        Text("Code an \(email)").font(.callout).foregroundStyle(.secondary)
                        TextField("6-stelliger Code", text: $code).textFieldStyle(.roundedBorder).font(.title3)
                        Button {
                            Task { _ = await account.verify(email: email, code: code) }
                        } label: { Text("Bestätigen").frame(maxWidth: .infinity) }
                            .buttonStyle(.borderedProminent).controlSize(.large)
                            .disabled(code.filter(\.isNumber).count < 6 || account.busy)
                        HStack {
                            Button("Code erneut senden") { Task { await account.resend(email: email) } }.buttonStyle(.link)
                            Spacer()
                            Button("Zurück") { mode = .login; account.problem = nil }.buttonStyle(.link)
                        }
                    } else {
                        Picker("", selection: $mode) {
                            Text("Anmelden").tag(Mode.login)
                            Text("Registrieren").tag(Mode.register)
                        }.pickerStyle(.segmented).labelsHidden()

                        TextField("E-Mail", text: $email).textFieldStyle(.roundedBorder)
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
                    if account.busy { ProgressView().controlSize(.small) }
                    if let p = account.problem {
                        Label(p, systemImage: "exclamationmark.triangle.fill").foregroundStyle(.orange).font(.callout)
                    }
                }
                .clanCard()
                .frame(maxWidth: 380)
                Text("Mit einem Konto gilt die [Datenschutzerklärung](\(Account.privacyURL.absoluteString)). Deine Sounds bleiben auf deinen Geräten.")
                    .font(.caption).foregroundStyle(.secondary).multilineTextAlignment(.center)
                    .frame(maxWidth: 380)
                Spacer()
            }
            .frame(maxWidth: .infinity)
            .padding(24)
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
    @State private var confirmDelete = false
    @State private var password = ""

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 22) {
                // Profil
                HStack(spacing: 12) {
                    Avatar(name: account.user?.name ?? "?")
                    VStack(alignment: .leading, spacing: 2) {
                        Text(account.user?.name ?? "").font(.headline)
                        Text(account.user?.email ?? "").font(.caption).foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button("Abmelden", role: .destructive) { account.logout() }.controlSize(.small)
                }
                .clanCard()

                // Clans
                VStack(alignment: .leading, spacing: 10) {
                    Text("Deine Clans").font(.title3.weight(.semibold))
                    if account.clans.isEmpty {
                        Text("Noch in keinem Clan. Leg unten einen an oder tritt mit einem Invite-Code bei.")
                            .foregroundStyle(.secondary).clanCard()
                    } else {
                        ForEach(account.clans) { c in
                            NavigationLink(value: c) { ClanRow(clan: c) }.buttonStyle(.plain)
                        }
                    }
                }

                // Anlegen + Beitreten nebeneinander
                HStack(alignment: .top, spacing: 12) {
                    VStack(alignment: .leading, spacing: 10) {
                        Label("Neuer Clan", systemImage: "plus.circle.fill").font(.subheadline.weight(.semibold))
                        TextField("Clan-Name", text: $newClan).textFieldStyle(.roundedBorder)
                        Button("Anlegen") {
                            Task { if let inv = await account.createClan(name: newClan) { note = "Clan angelegt. Invite-Code: \(inv)"; newClan = "" } }
                        }
                        .disabled(newClan.trimmingCharacters(in: .whitespaces).count < 2 || account.busy)
                    }.clanCard()

                    VStack(alignment: .leading, spacing: 10) {
                        Label("Clan beitreten", systemImage: "person.badge.plus").font(.subheadline.weight(.semibold))
                        TextField("Invite-Code oder Link", text: $joinCode).textFieldStyle(.roundedBorder)
                        Button("Beitreten") {
                            Task {
                                if await account.joinClan(code: joinCode) != nil { note = "Du bist jetzt im Clan."; joinCode = "" }
                            }
                        }
                        .disabled(joinCode.trimmingCharacters(in: .whitespaces).count < 4 || account.busy)
                    }.clanCard()
                }

                if let note { Text(note).font(.callout).foregroundStyle(.secondary) }
                if let p = account.problem { Label(p, systemImage: "exclamationmark.triangle.fill").foregroundStyle(.orange).font(.callout) }

                // Datenschutz
                HStack {
                    Link("Datenschutzerklärung", destination: Account.privacyURL)
                    Spacer()
                    Button("Konto löschen …", role: .destructive) { password = ""; confirmDelete = true }
                        .buttonStyle(.link).foregroundStyle(.red)
                }
                .font(.callout)
            }
            .padding(24)
        }
        .task { await account.refreshMe() }
        .alert("Konto wirklich löschen?", isPresented: $confirmDelete) {
            SecureField("Passwort", text: $password)
            Button("Endgültig löschen", role: .destructive) {
                Task { _ = await account.deleteAccount(password: password) }
            }
            Button("Abbrechen", role: .cancel) {}
        } message: {
            Text("E-Mail, Name, Passwort und Mitgliedschaften werden gelöscht. Clans, die dir gehören, übernimmt ein anderes Mitglied; bist du allein drin, wird der Clan aufgelöst. Das kann man nicht rückgängig machen.")
        }
    }
}

/// Eine Clan-Karte in der Liste.
struct ClanRow: View {
    let clan: ClanSummary
    var body: some View {
        HStack(spacing: 12) {
            Image(systemName: clan.isAdmin ? "crown.fill" : "person.3.fill")
                .font(.title3)
                .foregroundStyle(clan.isAdmin ? Color.orange : Color.secondary)
                .frame(width: 28)
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
