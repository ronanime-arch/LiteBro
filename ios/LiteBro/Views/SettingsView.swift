import SwiftUI

struct SettingsView: View {
    @EnvironmentObject var browser: Browser
    @Environment(\.dismiss) private var dismiss
    @AppStorage(Prefs.themeKey) private var theme = "dark"
    @AppStorage(Prefs.searchKey) private var search = "google"
    @AppStorage(Prefs.unloadKey) private var unloadAfter = 5
    @AppStorage(Prefs.allowHostsKey) private var allowHosts = ""
    @State private var cleared = false

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    Toggle(L("Только localhost"), isOn: Binding(get: { browser.localOnly }, set: { browser.setLocalOnly($0) }))
                    TextField(L("Разрешённые сайты: x.com *.y.com"), text: $allowHosts, axis: .vertical)
                        .textInputAutocapitalization(.never).autocorrectionDisabled()
                        .onSubmit { browser.rulesChanged() }
                        .onChange(of: allowHosts) { _ in browser.rulesChanged() }
                } header: {
                    Text(L("Сеть"))
                } footer: {
                    Text(L("Открываются только этот iPhone, локальная сеть (192.168.*, 10.*, *.local, *.localhost) и разрешённые сайты. Остальные запросы, в том числе веб-сокеты и запросы воркеров, блокирует движок."))
                }

                Section {
                    Toggle(L("Блокировать рекламу и телеметрию"), isOn: Binding(get: { browser.adBlock }, set: { browser.setAdBlock($0) }))
                    if !Prefs.adBlockOff.isEmpty {
                        NavigationLink(L("Сайты без блокировки")) { AdBlockSites() }
                    }
                } header: {
                    Text(L("Защита"))
                } footer: {
                    Text(L("Рекламные сети, счётчики, запись сеансов и отчёты об ошибках блокирует сам движок, почти без затрат памяти. Реклама в видео YouTube проматывается. Проверка адресов Safe Browsing выключена."))
                }

                Section {
                    Picker(L("Тема"), selection: $theme) {
                        Text(L("Тёмная")).tag("dark")
                        Text(L("Светлая")).tag("light")
                        Text(L("Как в системе")).tag("auto")
                    }
                    Picker(L("Поиск"), selection: $search) {
                        Text(verbatim: "Google").tag("google")
                        Text(verbatim: "DuckDuckGo").tag("duckduckgo")
                    }
                } header: {
                    Text(L("Оформление"))
                }

                Section {
                    Stepper(value: $unloadAfter, in: 0...120) {
                        Text(unloadAfter == 0 ? L("Не выгружать") : L("Выгружать через, мин:") + " \(unloadAfter)")
                    }
                } header: {
                    Text(L("Вкладки"))
                } footer: {
                    Text(L("Фоновая вкладка отдаёт память, страница загрузится снова при открытии; история остаётся. Вкладки со звуком не выгружаются."))
                }

                Section {
                    Button(role: .destructive) {
                        browser.clearSiteData { cleared = true }
                    } label: {
                        Text(cleared ? L("Удалено") : L("Удалить cookies, кэш и данные сайтов"))
                    }
                    .disabled(cleared)
                }

                Section {
                    Text(verbatim: "LiteBro " + (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? ""))
                        .foregroundStyle(.secondary)
                }
            }
            .navigationTitle(L("Настройки"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button(L("Готово")) { dismiss() } } }
        }
    }
}

/// The sites where "Do not block ads on this site" is on
private struct AdBlockSites: View {
    @EnvironmentObject var browser: Browser
    @State private var sites = Prefs.adBlockOff

    var body: some View {
        List {
            ForEach(sites, id: \.self) { Text(verbatim: $0) }
                .onDelete { i in
                    sites.remove(atOffsets: i)
                    Prefs.adBlockOff = sites
                    browser.rulesChanged()
                }
        }
        .navigationTitle(L("Сайты без блокировки"))
    }
}
