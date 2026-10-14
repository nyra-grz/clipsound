import SwiftUI

/// Konto-Fenster: nicht eingeloggt → Anmelden/Registrieren/Bestätigen; eingeloggt → Clans.
struct AccountSheet: View {
    @ObservedObject var account: Account
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Text(account.loggedIn ? "Dein Konto & Clans" : "Anmelden").font(.headline)
                Spacer()
                Button("Fertig") { dismiss() }
            }
            .padding(12)
            Divider()
            if account.loggedIn { AccountHome(account: account) }
            else { AccountAuth(account: account) }
        }
        .frame(width: 440, height: 500)
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
                    Button("Bestätigen") { Task { _ = await account.verify(email: email, code: code) } }
                        .disabled(code.filter(\.isNumber).count < 6 || account.busy)
                    Button("Code erneut senden") { Task { await account.resend(email: email) } }
                        .buttonStyle(.link)
                    Button("Zurück") { mode = .login; account.problem = nil }
                        .buttonStyle(.link)
                }
            } else {
                Section {
                    TextField("E-Mail", text: $email)
                    if mode == .register { TextField("Name", text: $name) }
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
                    .buttonStyle(.borderedProminent)
                    .disabled(account.busy || email.isEmpty || password.isEmpty || (mode == .register && name.count < 2))
                }
            }

            if account.busy { Section { ProgressView() } }
            if let p = account.problem {
                Section { Label(p, systemImage: "exclamationmark.triangle.fill").foregroundStyle(.orange) }
            }
        }
        .formStyle(.grouped)
    }
}

private struct AccountHome: View {
    @ObservedObject var account: Account
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
                    HStack(spacing: 8) {
                        Image(systemName: c.isAdmin ? "crown.fill" : "person.3.fill")
                            .foregroundStyle(c.isAdmin ? Color.orange : Color.secondary)
                        Text(c.name)
                        Spacer()
                        Text("\(c.members) \(c.members == 1 ? "Mitglied" : "Mitglieder")")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }
            }

            Section("Neuen Clan anlegen") {
                HStack {
                    TextField("Clan-Name", text: $newClan)
                    Button("Anlegen") {
                        Task {
                            if let inv = await account.createClan(name: newClan) {
                                note = "Clan angelegt. Invite-Code zum Weitergeben: \(inv)"
                                newClan = ""
                            }
                        }
                    }
                    .disabled(newClan.trimmingCharacters(in: .whitespaces).count < 2 || account.busy)
                }
            }

            Section("Clan beitreten") {
                HStack {
                    TextField("Invite-Code", text: $joinCode)
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
            }

            if let n = note { Section { Text(n).foregroundStyle(.secondary) } }
            if let p = account.problem {
                Section { Label(p, systemImage: "exclamationmark.triangle.fill").foregroundStyle(.orange) }
            }
        }
        .formStyle(.grouped)
        .task { await account.refreshMe() }
    }
}
