import SwiftUI

struct SettingsView: View {
    @EnvironmentObject var browser: Browser
    @Environment(\.dismiss) private var dismiss
    @AppStorage(Prefs.themeKey) private var theme = "dark"
    @AppStorage(Prefs.searchKey) private var search = "google"
    @AppStorage(SearchCountry.key) private var country = ""
    @AppStorage(Prefs.unloadKey) private var unloadAfter = 5
    @AppStorage(Prefs.allowHostsKey) private var allowHosts = ""
    @State private var cleared = false
    @State private var safeAsked = false

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
                    Button {
                        if let u = URL(string: UIApplication.openSettingsURLString) { UIApplication.shared.open(u) }
                    } label: {
                        Label(L("Сделать браузером по умолчанию"), systemImage: "safari")
                    }
                } footer: {
                    // Entitlements are fixed at signing: the variant is chosen by the .tipa, not here
                    Text(FileShare.shared.wholePhone
                         ? L("Эта сборка (LiteBro.tipa) даёт ПК доступ ко всему телефону, поэтому TrollStore ставит её как системную, и браузером по умолчанию её не выбрать. Нужен браузер по умолчанию — поставьте поверх LiteBro-Browser.tipa из того же архива; вернуть доступ ко всему телефону — снова LiteBro.tipa.")
                         : L("Откроются настройки LiteBro: «Приложение браузера по умолчанию» → LiteBro. Тогда ссылки из других приложений открываются здесь.") + " "
                           + L("В этой сборке (LiteBro-Browser.tipa) ПК видит только папку LiteBro. Доступ ко всему телефону — LiteBro.tipa из того же архива, поверх."))
                }

                Section {
                    Toggle(L("Блокировать рекламу и телеметрию"), isOn: Binding(get: { browser.adBlock }, set: { browser.setAdBlock($0) }))
                    Toggle(L("Защита от вредных сайтов"), isOn: Binding(get: { Prefs.protect }, set: { browser.setProtect($0) }))
                    if !Prefs.adBlockOff.isEmpty {
                        NavigationLink(L("Сайты без блокировки")) { AdBlockSites() }
                    }
                    HStack {
                        Text(L("Списки фильтров"))
                        Spacer()
                        if let st = browser.listsState {
                            ProgressView().padding(.trailing, 4)
                            Text(st).foregroundStyle(.secondary)
                        } else if let d = FilterLists.updated {
                            Text(d.formatted(date: .abbreviated, time: .shortened)).foregroundStyle(.secondary)
                        } else {
                            Text(L("встроенный список")).foregroundStyle(.secondary)
                        }
                    }
                    Button(L("Обновить списки")) { browser.refreshLists(force: true) }
                        .disabled(browser.listsState != nil)
                } header: {
                    Text(L("Защита"))
                } footer: {
                    Text(L("Реклама и счётчики — списки EasyList, RU AdList и EasyPrivacy, их применяет сам движок, почти без затрат памяти; реклама в видео YouTube проматывается. Вредные сайты — списки uBlock Badware, URLhaus и Phishing Army и проверка Safe Browsing: такой сайт не откроется без вашего согласия. Списки обновляются раз в 4 дня."))
                }

                Section {
                    NavigationLink { PasswordsView() } label: { Label(L("Пароли"), systemImage: "key") }
                    NavigationLink { CardsView() } label: { Label(L("Способы оплаты"), systemImage: "creditcard") }
                } header: {
                    Text(L("Пароли и карты"))
                } footer: {
                    Text(L("Хранятся в связке ключей этого устройства, без iCloud, и подставляются только после Face ID или код-пароля. Кнопка «Пароли» над клавиатурой — это пароли iCloud, их тоже можно выбрать."))
                }

                Section {
                    Toggle(L("Консоль с начала загрузки страницы"), isOn: Binding(get: { Prefs.console }, set: { browser.setConsole($0) }))
                } header: {
                    Text(L("Для разработчика"))
                } footer: {
                    Text(L("Консоль страницы — в меню «…». Без переключателя она собирает сообщения только после открытия и не тратит ресурсы на остальных страницах."))
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
                    Picker(L("Страна поиска"), selection: $country) {
                        Text(L("Как обычно")).tag("")
                        ForEach(SearchCountry.all) { c in Text(verbatim: c.name).tag(c.code) }
                    }
                } header: {
                    Text(L("Оформление"))
                } footer: {
                    Text(L("Страна поиска — результаты Google как из этой страны (параметры gl и hl, без прокси и VPN). На странице результатов она же — в меню «…»."))
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
                    NavigationLink { DebugLogView() } label: { Label(L("Журнал отладки"), systemImage: "stethoscope") }
                    Button {
                        Debug.safeNextLaunch()
                        safeAsked = true
                    } label: {
                        Text(safeAsked ? L("Следующий запуск — в безопасном режиме") : L("Безопасный режим при следующем запуске"))
                    }
                    .disabled(safeAsked)
                } header: {
                    Text(L("Отладка"))
                } footer: {
                    Text(L("Журнал запуска и падений лежит в «Файлы» › «На iPhone» › LiteBro › Debug, его можно открыть, даже если LiteBro не запускается. После запуска, который не дошёл до конца, LiteBro открывается в безопасном режиме: без сохранённых вкладок, списков фильтров и правил блокировки."))
                }

                Section {
                    Text(verbatim: "LiteBro " + (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "")
                         + " (" + Debug.build + ")")
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
