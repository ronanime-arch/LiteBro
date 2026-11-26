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
                                           : L("Папка LiteBro в «Файлах»: TrollStore не дал доступа ко всей памяти."))
                         + " " + L("На ПК откройте адрес в браузере или подключите его в Проводнике: «Этот компьютер» → «Подключить сетевой диск». Пока доступ открыт, LiteBro должен оставаться на экране: телефон не будет гаснуть."))
                }

                Section {
                    if let u = screenURL, let until = screenUntil, until > Date() {
                        address(u)
                        BroadcastButton()
                            .frame(height: 44)
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
                    Text(L("Показ идёт через запись экрана iOS: нажмите кнопку выше или в Пункте управления удержите «Запись экрана» и выберите LiteBro. Пока идёт показ, вверху горит красная полоска; остановить — нажать на неё. Показ работает и в других приложениях. Управление касаниями — через закрытые функции iOS: на странице показа мышь работает как палец, есть кнопки «Домой» и боковая; может не заработать."))
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
                    ShareLink(item: url) { Label(L("Поделиться"), systemImage: "square.and.arrow.up") }
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

/// The system button that starts the screen recording into LiteBro's extension
private struct BroadcastButton: UIViewRepresentable {
    func makeUIView(context: Context) -> RPSystemBroadcastPickerView {
        let v = RPSystemBroadcastPickerView(frame: CGRect(x: 0, y: 0, width: 44, height: 44))
        v.preferredExtension = "io.github.ronanime-arch.LiteBro.Screen"
        v.showsMicrophoneButton = false
        return v
    }
    func updateUIView(_ v: RPSystemBroadcastPickerView, context: Context) {}
}
