import SwiftUI
import AppKit

// MARK: - Gemeinsame Bausteine

/// Rundes Monogramm aus den Initialen, Farbe pro Name.
struct Avatar: View {
    let name: String
    var size: CGFloat = 40
    private var initials: String {
        let parts = name.split(separator: " ").prefix(2)
        let s = parts.compactMap { $0.first }.map(String.init).joined()
        return s.isEmpty ? "?" : s.uppercased()
    }
    var body: some View {
        Circle().fill(Palette.gradient(Palette.color(for: name)))
            .frame(width: size, height: size)
            .overlay(Text(initials).font(.system(size: size * 0.4, weight: .semibold, design: .rounded)).foregroundStyle(.white))
            .overlay(Circle().strokeBorder(.white.opacity(0.18), lineWidth: 1))
    }
}

/// Clan-Symbol: Squircle mit dem ersten Buchstaben, Farbe pro Clan.
struct ClanIcon: View {
    let name: String
    let key: String
    var size: CGFloat = 32
    var body: some View {
        RoundedRectangle(cornerRadius: size * 0.3, style: .continuous)
            .fill(Palette.gradient(Palette.color(for: key)))
            .frame(width: size, height: size)
            .overlay {
                Text(name.first.map { String($0).uppercased() } ?? "?")
                    .font(.system(size: size * 0.5, weight: .bold, design: .rounded))
                    .foregroundStyle(.white)
            }
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

// MARK: - Seite „Konto“

struct AccountPage: View {
    @ObservedObject var account: Account
    let openClan: (String) -> Void

    var body: some View {
        Group {
            if account.loggedIn { AccountHome(account: account, openClan: openClan) }
            else { AccountAuth(account: account) }
        }
        .navigationTitle("Konto")
        .navigationSubtitle(account.loggedIn ? "Konto & Clans" : "Anmelden")
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
            VStack(spacing: 22) {
                IconTile(systemImage: mode == .verify ? "envelope.badge.fill" : "person.3.sequence.fill",
                         color: .orange, size: 84)
                VStack(spacing: 6) {
                    Text(mode == .verify ? "E-Mail bestätigen" : "ClipSound-Clans")
                        .font(.largeTitle.weight(.bold))
                    Text(mode == .verify ? "Gib den Code aus der E-Mail ein." : "Melde dich an, um Clans beizutreten und zusammen abzuspielen.")
                        .font(.title3)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                }

                VStack(spacing: 14) {
                    if mode == .verify {
                        Text("Code an \(email)").font(.callout).foregroundStyle(.secondary)
                        TextField("6-stelliger Code", text: $code)
                            .textFieldStyle(.roundedBorder)
                            .font(.title3.monospacedDigit())
                            .multilineTextAlignment(.center)
                        Button {
                            Task { _ = await account.verify(email: email, code: code) }
                        } label: { Text("Bestätigen").frame(maxWidth: .infinity) }
                            .glassButton(prominent: true)
                            .disabled(code.filter(\.isNumber).count < 6 || account.busy)
                        HStack {
                            Button("Code erneut senden") { Task { await account.resend(email: email) } }.buttonStyle(.link)
                            Spacer()
                            Button("Zurück") { mode = .login; account.problem = nil }.buttonStyle(.link)
                        }
                        .font(.callout)
                    } else {
                        Picker("", selection: $mode) {
                            Text("Anmelden").tag(Mode.login)
                            Text("Registrieren").tag(Mode.register)
                        }
                        .pickerStyle(.segmented)
                        .labelsHidden()

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
                            .glassButton(prominent: true)
                            .keyboardShortcut(.defaultAction)
                            .disabled(account.busy || email.isEmpty || password.isEmpty || (mode == .register && name.count < 2))
                    }
                    if account.busy { ProgressView().controlSize(.small) }
                    if let p = account.problem {
                        Label(p, systemImage: "exclamationmark.triangle.fill").foregroundStyle(.orange).font(.callout)
                    }
                }
                .controlSize(.large)
                .card(padding: 22)
                .frame(maxWidth: 400)

                Text("Mit einem Konto gilt die [Datenschutzerklärung](\(Account.privacyURL.absoluteString)). Deine Sounds bleiben auf deinen Geräten.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: 400)
            }
            .padding(32)
            .padding(.top, 12)
            .frame(maxWidth: .infinity)
        }
        .softScrollEdges()
        .animation(.snappy, value: mode)
    }
}

// MARK: - Konto + Clans

private struct AccountHome: View {
    @ObservedObject var account: Account
    let openClan: (String) -> Void
    @State private var newClan = ""
    @State private var joinCode = ""
    @State private var note: String?
    @State private var confirmDelete = false
    @State private var password = ""

    var body: some View {
        Form {
            // Profil
            Section {
                HStack(spacing: 16) {
                    Avatar(name: account.user?.name ?? "?", size: 60)
                    VStack(alignment: .leading, spacing: 3) {
                        Text(account.user?.name ?? "").font(.title2.weight(.bold))
                        Text(account.user?.email ?? "").foregroundStyle(.secondary)
                    }
                    Spacer()
                    Button("Abmelden") { account.logout() }
                }
                .padding(.vertical, 6)
            }

            Section("Deine Clans") {
                if account.clans.isEmpty {
                    Text("Noch in keinem Clan. Leg unten einen an oder tritt mit einem Invite-Code bei.")
                        .foregroundStyle(.secondary)
                }
                ForEach(account.clans) { c in
                    Button { openClan(c.id) } label: { ClanRow(clan: c) }
                        .buttonStyle(.plain)
                }
            }

            Section {
                LabeledContent("Neuer Clan") {
                    HStack {
                        TextField("Neuer Clan", text: $newClan, prompt: Text("Clan-Name"))
                            .labelsHidden()
                            .frame(maxWidth: 220)
                        Button("Anlegen") {
                            Task { if let inv = await account.createClan(name: newClan) { note = "Clan angelegt. Invite-Code: \(inv)"; newClan = "" } }
                        }
                        .disabled(newClan.trimmingCharacters(in: .whitespaces).count < 2 || account.busy)
                    }
                }
                LabeledContent("Clan beitreten") {
                    HStack {
                        TextField("Clan beitreten", text: $joinCode, prompt: Text("Invite-Code oder Link"))
                            .labelsHidden()
                            .frame(maxWidth: 220)
                        Button("Beitreten") {
                            Task {
                                if await account.joinClan(code: joinCode) != nil { note = "Du bist jetzt im Clan."; joinCode = "" }
                            }
                        }
                        .disabled(joinCode.trimmingCharacters(in: .whitespaces).count < 4 || account.busy)
                    }
                }
            } footer: {
                VStack(alignment: .leading, spacing: 4) {
                    if let note { Text(note).foregroundStyle(.secondary) }
                    if let p = account.problem { Label(p, systemImage: "exclamationmark.triangle.fill").foregroundStyle(.orange) }
                }
                .font(.callout)
            }

            Section {
                Link(destination: Account.privacyURL) {
                    Label("Datenschutzerklärung", systemImage: "hand.raised.fill")
                }
                Button("Konto löschen …", role: .destructive) { password = ""; confirmDelete = true }
                    .foregroundStyle(.red)
            }
        }
        .formStyle(.grouped)
        .softScrollEdges()
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

/// Eine Clan-Zeile in der Liste.
struct ClanRow: View {
    let clan: ClanSummary
    var body: some View {
        HStack(spacing: 12) {
            ClanIcon(name: clan.name, key: clan.id, size: 34)
            VStack(alignment: .leading, spacing: 2) {
                Text(clan.name).font(.body.weight(.semibold))
                Text("\(clan.members) \(clan.members == 1 ? "Mitglied" : "Mitglieder")").font(.caption).foregroundStyle(.secondary)
            }
            Spacer()
            Badge(text: clan.isAdmin ? "Admin" : "Mitglied", color: clan.isAdmin ? .orange : .secondary)
            Image(systemName: "chevron.right").font(.caption.weight(.semibold)).foregroundStyle(.tertiary)
        }
        .padding(.vertical, 2)
        .contentShape(Rectangle())
    }
}
