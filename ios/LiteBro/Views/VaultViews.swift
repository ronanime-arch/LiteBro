import SwiftUI
import WebKit

/// The bar above the toolbar: log in with a saved password, save a new one, fill in a card
struct OfferBar: View {
    @ObservedObject var tab: BrowserTab
    @ObservedObject private var vault = Vault.shared

    var body: some View {
        if let offer = tab.offer {
            HStack(spacing: 10) {
                switch offer {
                case .login(let host, let frame):
                    Image(systemName: "key.fill").foregroundStyle(.tint)
                    let list = vault.logins(for: host)
                    if list.count == 1, let l = list.first {
                        Button(L("Войти как") + " " + (l.user.isEmpty ? host : l.user)) { fill(l, frame) }
                    } else {
                        Menu(L("Войти с сохранённым паролем")) {
                            ForEach(list) { l in Button(l.user.isEmpty ? l.host : l.user) { fill(l, frame) } }
                        }
                    }
                case .save(let host, let user, let pass):
                    Image(systemName: "key").foregroundStyle(.tint)
                    VStack(alignment: .leading, spacing: 0) {
                        Text(L("Сохранить пароль?")).font(.subheadline.bold())
                        Text(verbatim: (user.isEmpty ? "" : user + " · ") + host).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                    }
                    Spacer()
                    Menu {
                        Button(L("Не сейчас")) { tab.offer = nil }
                        Button(L("Никогда для этого сайта")) { Prefs.vaultNever.append(host); tab.offer = nil }
                    } label: { Text(L("Нет")) }
                    Button(L("Сохранить")) { vault.save(host: host, user: user, pass: pass); tab.offer = nil }
                        .buttonStyle(.borderedProminent)
                case .card(let frame):
                    Image(systemName: "creditcard.fill").foregroundStyle(.tint)
                    Menu(L("Заполнить карту")) {
                        ForEach(vault.cards) { c in Button(c.label) { fill(c, frame) } }
                    }
                }
                if !Self.asks(offer) {
                    Spacer()
                    Button { tab.offer = nil } label: { Image(systemName: "xmark").foregroundStyle(.secondary) }
                        .accessibilityLabel(L("Закрыть"))
                }
            }
            .font(.subheadline)
            .padding(.horizontal, 14).padding(.vertical, 8)
            .background(.bar)
        }
    }

    /// Saving has its own buttons, the others a close button
    private static func asks(_ o: BrowserTab.Offer) -> Bool {
        if case .save = o { return true }
        return false
    }

    private func fill(_ l: Vault.Login, _ frame: WKFrameInfo) {
        vault.password(l, reason: L("Подставить пароль для") + " " + l.host) { pass in
            guard let pass else { return }
            let js = "window.__litebroVault && window.__litebroVault.login(" + JS.string(l.user) + ", " + JS.string(pass) + ")"
            tab.webView?.evaluateJavaScript(js, in: frame, in: .defaultClient) { _ in }
            tab.offer = nil
        }
    }

    private func fill(_ c: Vault.CardInfo, _ frame: WKFrameInfo) {
        vault.card(c, reason: L("Подставить карту") + " " + c.label) { card in
            guard let card, let data = try? JSONEncoder().encode(card) else { return }
            let js = "window.__litebroVault && window.__litebroVault.card(" + String(decoding: data, as: UTF8.self) + ")"
            tab.webView?.evaluateJavaScript(js, in: frame, in: .defaultClient) { _ in }
            tab.offer = nil
        }
    }
}

enum JS {
    /// A Swift string as a JavaScript string literal
    static func string(_ s: String) -> String {
        let d = (try? JSONSerialization.data(withJSONObject: [s])) ?? Data("[\"\"]".utf8)
        return String(String(decoding: d, as: UTF8.self).dropFirst().dropLast())
    }
}

struct PasswordsView: View {
    @ObservedObject private var vault = Vault.shared
    @State private var shown: [String: String] = [:]

    var body: some View {
        List {
            if vault.logins.isEmpty {
                Text(L("Пароли появятся здесь, когда вы сохраните их при входе на сайт.")).foregroundStyle(.secondary)
            }
            ForEach(vault.logins) { l in
                VStack(alignment: .leading, spacing: 3) {
                    Text(verbatim: l.host).font(.headline)
                    Text(verbatim: l.user.isEmpty ? "—" : l.user).foregroundStyle(.secondary)
                    if let p = shown[l.id] {
                        Text(verbatim: p).font(.body.monospaced()).textSelection(.enabled)
                    }
                }
                .contextMenu {
                    Button { reveal(l) } label: { Label(L("Показать пароль"), systemImage: "eye") }
                    Button { copy(l) } label: { Label(L("Копировать пароль"), systemImage: "doc.on.doc") }
                    Button { UIPasteboard.general.string = l.user } label: { Label(L("Копировать имя"), systemImage: "person") }
                }
                .swipeActions { Button(role: .destructive) { vault.delete(l) } label: { Label(L("Удалить"), systemImage: "trash") } }
            }
        }
        .navigationTitle(L("Пароли"))
        .onDisappear { shown.removeAll() }
    }

    private func reveal(_ l: Vault.Login) {
        vault.password(l, reason: L("Показать пароль для") + " " + l.host) { if let p = $0 { shown[l.id] = p } }
    }

    private func copy(_ l: Vault.Login) {
        vault.password(l, reason: L("Копировать пароль для") + " " + l.host) { p in
            guard let p else { return }
            // Gone from the clipboard in 90 seconds and not handed to other devices
            UIPasteboard.general.setItems([["public.utf8-plain-text": p]],
                                          options: [.expirationDate: Date().addingTimeInterval(90), .localOnly: true])
        }
    }
}

struct CardsView: View {
    @ObservedObject private var vault = Vault.shared
    @State private var adding = false

    var body: some View {
        List {
            Section {
                ForEach(vault.cards) { c in
                    Label(c.label, systemImage: "creditcard")
                        .swipeActions { Button(role: .destructive) { vault.deleteCard(c) } label: { Label(L("Удалить"), systemImage: "trash") } }
                }
                Button { adding = true } label: { Label(L("Добавить карту"), systemImage: "plus") }
            } footer: {
                Text(L("Карты хранятся в связке ключей только этого устройства, зашифрованными; каждое заполнение спрашивает Face ID или код-пароль. Код CVC не сохраняется — его вводите сами."))
            }
        }
        .navigationTitle(L("Способы оплаты"))
        .sheet(isPresented: $adding) { CardEditor() }
    }
}

private struct CardEditor: View {
    @Environment(\.dismiss) private var dismiss
    @State private var name = ""
    @State private var number = ""
    @State private var month = Calendar.current.component(.month, from: Date())
    @State private var year = Calendar.current.component(.year, from: Date())

    private var valid: Bool { Vault.luhn(number) }

    var body: some View {
        NavigationStack {
            Form {
                TextField(L("Номер карты"), text: $number).keyboardType(.numberPad).textContentType(.creditCardNumber)
                if !number.isEmpty && !valid {
                    Text(L("Номер не похож на номер карты")).font(.caption).foregroundStyle(.red)
                }
                TextField(L("Имя на карте"), text: $name).textContentType(.name).textInputAutocapitalization(.characters)
                Picker(L("Месяц"), selection: $month) { ForEach(1...12, id: \.self) { Text(verbatim: String(format: "%02d", $0)).tag($0) } }
                let now = Calendar.current.component(.year, from: Date())
                Picker(L("Год"), selection: $year) { ForEach(now...(now + 15), id: \.self) { Text(verbatim: String($0)).tag($0) } }
            }
            .navigationTitle(L("Новая карта"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button(L("Отмена")) { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button(L("Сохранить")) {
                        Vault.shared.addCard(.init(name: name, number: number.filter(\.isNumber), month: month, year: year))
                        dismiss()
                    }
                    .disabled(!valid)
                }
            }
        }
    }
}
