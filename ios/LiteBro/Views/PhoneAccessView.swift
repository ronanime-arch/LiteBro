import LocalAuthentication
import ReplayKit
import SwiftUI

/// «Доступ с ПК»: the phone's files and screen for a computer in the same Wi-Fi network, each after Face ID
struct PhoneAccessView: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject private var files = FileShare.shared
    @AppStorage("filesMinutes") private var minutes = 30
    @AppStorage("filesReadOnly") private var readOnly = false
    @AppStorage("screenControl") private var control = false
    @State private var screenURL: String?
    @State private var screenUntil: Date?
    @State private var screenError: String?

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    if files.running, let u = files.url {
                        address(u)
                        if let t = files.until {
                            Text(L("Закроется в") + " " + t.formatted(date: .omitted, time: .shortened)).foregroundStyle(.secondary)
                        }
                        Button(L("Закрыть доступ"), role: .destructive) { files.stop() }
                    } else {
                        Picker(L("На время"), selection: $minutes) {
                            Text(L("15 минут")).tag(15)
                            Text(L("30 минут")).tag(30)
                            Text(L("1 час")).tag(60)
                            Text(L("Пока не закрою")).tag(0)
                        }
                        Toggle(L("Только чтение"), isOn: $readOnly)
                        Button { files.start(minutes: minutes, readOnly: readOnly) } label: {
                            Label(L("Открыть доступ к файлам"), systemImage: "faceid")
                        }
                    }
                    if let e = files.error { Text(e).foregroundStyle(.red) }
                } header: {
                    Text(L("Файлы"))
                } footer: {
                    Text((files.wholePhone ? L("Вся память телефона: фото (Media/DCIM), загрузки и данные приложений.")
                                           : L("Папка LiteBro в «Файлах»: загрузки и файлы приложения."))
                         + " " + L("На ПК откройте адрес в браузере или подключите его в Проводнике: «Этот компьютер» → «Подключить сетевой диск». Пока доступ открыт, LiteBro должен оставаться на экране: телефон не будет гаснуть."))
                }

                Section {
                    if let u = screenURL, let until = screenUntil, until > Date() {
                        address(u)
                        ScreenStatus()
                        BroadcastButton()
                            .frame(maxWidth: .infinity, minHeight: 44)
                        Button(L("Запретить показ"), role: .destructive) {
                            ScreenShare.revoke()
                            screenURL = nil
                        }
                        if ScreenShare.control { Text(L("С управлением касаниями")).foregroundStyle(.secondary) }
                    } else {
                        Toggle(L("Управление касаниями (экспериментально)"), isOn: $control)
                        Button { allowScreen() } label: { Label(L("Разрешить показ экрана"), systemImage: "faceid") }
                    }
                    if let e = screenError { Text(e).foregroundStyle(.red) }
                } header: {
                    Text(L("Экран"))
                } footer: {
                    Text(L("Адрес отвечает, только пока идёт показ. Показ идёт через запись экрана iOS: нажмите «Начать показ» или в Пункте управления удержите «Запись экрана» и выберите LiteBro. Пока идёт показ, вверху горит красная полоска; остановить — нажать на неё. Показ работает и в других приложениях. Управление касаниями — через закрытые функции iOS: на странице показа мышь работает как палец, есть кнопки «Домой» и боковая; может не заработать."))
                }
            }
            .navigationTitle(L("Доступ с ПК"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button(L("Готово")) { dismiss() } } }
            .onAppear {
                if let t = ScreenShare.token, let ip = MiniHTTP.address() {
                    screenURL = "http://\(ip):\(ScreenShare.port)/\(t)/"
                    screenUntil = ScreenShare.until
                }
            }
        }
    }

    private func address(_ u: String) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(verbatim: u).font(.system(.callout, design: .monospaced)).textSelection(.enabled)
            HStack {
                Button { UIPasteboard.general.string = u } label: { Label(L("Копировать"), systemImage: "doc.on.doc") }
                    .buttonStyle(.bordered)
                if let url = URL(string: u) {
                    ShareButton(item: url)
                        .buttonStyle(.bordered)
                }
            }
        }
    }

    private func allowScreen() {
        screenError = nil
        LAContext().evaluatePolicy(.deviceOwnerAuthentication, localizedReason: L("Показать экран телефона компьютеру")) { ok, _ in
            DispatchQueue.main.async {
                guard ok else { return }
                guard let ip = MiniHTTP.address() else { return screenError = L("Телефон не подключён к сети Wi-Fi.") }
                let t = ScreenShare.allow(minutes: minutes == 0 ? 24 * 60 : minutes)
                ScreenShare.control = control
                screenURL = "http://\(ip):\(ScreenShare.port)/\(t)/"
                screenUntil = ScreenShare.until
            }
        }
    }
}

/// Whether the screen goes out now: the address answers only while the screen recording into LiteBro runs
private struct ScreenStatus: View {
    @State private var live = false
    @State private var state = ScreenShare.state
    private let timer = Timer.publish(every: 1, on: .main, in: .common).autoconnect()

    var body: some View {
        Label {
            Text(text).font(.footnote)
        } icon: {
            Image(systemName: live ? "dot.radiowaves.left.and.right" : "pause.circle")
                .foregroundStyle(live ? .green : .orange)
        }
        .onReceive(timer) { _ in
            state = ScreenShare.state
            DispatchQueue.global(qos: .utility).async {
                let on = Self.answers()
                DispatchQueue.main.async { live = on }
            }
        }
    }

    private var text: String {
        if live { return L("Показ идёт: откройте адрес на ПК.") }
        if !ScreenShare.shared { return L("Показ не запустится: у этой подписи нет общей папки приложения и расширения (App Group).") }
        if let s = state?.text, s == "no-token" { return L("Запись экрана началась раньше, чем показ был разрешён: остановите её и запустите снова.") }
        if let s = state?.text, s.hasPrefix("port") { return L("Расширение не смогло открыть порт 8081:") + " " + s.dropFirst(6) }
        return L("Показ не идёт, поэтому на ПК «отказано в подключении». Нажмите «Начать показ» ниже, выберите LiteBro и «Начать вещание».")
    }

    /// The extension's server on this phone
    static func answers() -> Bool {
        let fd = socket(AF_INET, SOCK_STREAM, 0)
        guard fd >= 0 else { return false }
        defer { close(fd) }
        var a = sockaddr_in()
        a.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        a.sin_family = sa_family_t(AF_INET)
        a.sin_port = UInt16(ScreenShare.port).bigEndian
        a.sin_addr.s_addr = inet_addr("127.0.0.1")
        return withUnsafePointer(to: &a) {
            $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) == 0 }
        }
    }
}

/// The system button that starts the screen recording into LiteBro's extension, with a title
private struct BroadcastButton: UIViewRepresentable {
    func makeUIView(context: Context) -> RPSystemBroadcastPickerView {
        let v = RPSystemBroadcastPickerView(frame: CGRect(x: 0, y: 0, width: 220, height: 44))
        v.preferredExtension = "io.github.ronanime-arch.LiteBro.Screen"
        v.showsMicrophoneButton = false
        for case let b as UIButton in v.subviews {
            b.setTitle("  " + L("Начать показ"), for: .normal)
            b.contentHorizontalAlignment = .leading
            b.frame = v.bounds
            b.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        }
        return v
    }
    func updateUIView(_ v: RPSystemBroadcastPickerView, context: Context) {}
}
