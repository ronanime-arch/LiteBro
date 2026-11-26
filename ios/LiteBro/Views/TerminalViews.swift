import SwiftTerm
import SwiftUI
import UIKit

/// The computers for the terminal, and the terminal itself
struct TerminalHome: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject private var store = SSHStore.shared
    @State var path: [UUID]
    @State private var editing: SSHHost?
    @StateObject private var finder = PCFinder()

    var body: some View {
        NavigationStack(path: $path) {
            List {
                nearby
                Section {
                    ForEach(store.hosts) { h in
                        NavigationLink(value: h.id) {
                            VStack(alignment: .leading, spacing: 2) {
                                Text(verbatim: h.name)
                                Text(verbatim: h.user + "@" + h.host + (h.port == 22 ? "" : ":\(h.port)"))
                                    .font(.footnote).foregroundStyle(.secondary)
                            }
                        }
                        .swipeActions {
                            Button(role: .destructive) { store.remove(h) } label: { Label(L("Удалить"), systemImage: "trash") }
                            Button { editing = h } label: { Label(L("Изменить"), systemImage: "pencil") }
                        }
                    }
                    Button { editing = SSHHost(name: "", host: "", user: "") } label: {
                        Label(L("Добавить компьютер"), systemImage: "plus")
                    }
                } footer: {
                    Text(L("Компьютер с LiteBro в той же Wi-Fi появляется выше сам: «Подключить» и «Да» на компьютере. Иначе — QR-код: на компьютере в LiteBro стартовая страница → «С телефона» → «Терминал»."))
                }
            }
            .onAppear { finder.search() }
            .refreshable { finder.search() }
            .navigationTitle(L("Терминал ПК"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { ToolbarItem(placement: .confirmationAction) { Button(L("Готово")) { dismiss() } } }
            .navigationDestination(for: UUID.self) { id in
                if let h = store.hosts.first(where: { $0.id == id }) { TerminalScreen(host: h) }
            }
            .sheet(item: $editing) { h in HostEditor(host: h) }
        }
    }
}

extension TerminalHome {
    /// LiteBro on the computers of this Wi-Fi, not added yet (or added again with this iPhone's key)
    @ViewBuilder private var nearby: some View {
        let added = Set(store.hosts.filter(\.useKey).map { $0.host + "|" + $0.user })
        let list = finder.found.filter { !added.contains($0.id) || finder.pairing[$0.id] != nil }
        Section {
            ForEach(list) { f in
                HStack {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(verbatim: f.name)
                        Text(verbatim: f.user + "@" + f.ip).font(.footnote).foregroundStyle(.secondary)
                        switch finder.pairing[f.id] {
                        case .asking(let code):
                            Text(L("Нажмите «Да» на компьютере. Код там:") + " " + code)
                                .font(.footnote).foregroundStyle(.orange)
                        case .failed(let why):
                            Text(verbatim: why).font(.footnote).foregroundStyle(.red)
                        default:
                            EmptyView()
                        }
                    }
                    Spacer()
                    switch finder.pairing[f.id] {
                    case .asking:
                        ProgressView()
                    case .done(let h):
                        Button(L("Открыть")) { finder.pairing[f.id] = nil; path = [h.id] }.buttonStyle(.borderedProminent)
                    default:
                        Button(L("Подключить")) { finder.pair(f) }.buttonStyle(.borderedProminent)
                    }
                }
            }
            if list.isEmpty {
                HStack {
                    Text(finder.searching ? L("Ищу компьютеры с LiteBro…") : L("В этой сети компьютеров с LiteBro не видно."))
                        .foregroundStyle(.secondary)
                    Spacer()
                    if finder.searching { ProgressView() } else { Button(L("Искать снова")) { finder.search() } }
                }
            }
        } header: {
            Text(L("В этой сети"))
        }
    }
}

private struct HostEditor: View {
    @Environment(\.dismiss) private var dismiss
    @ObservedObject private var store = SSHStore.shared
    @State var host: SSHHost
    @State private var port = ""

    var body: some View {
        NavigationStack {
            Form {
                TextField(L("Название"), text: $host.name)
                TextField(L("Адрес, например 192.168.1.5"), text: $host.host)
                    .keyboardType(.URL).textInputAutocapitalization(.never).autocorrectionDisabled()
                TextField(L("Порт"), text: $port).keyboardType(.numberPad)
                TextField(L("Пользователь Windows"), text: $host.user)
                    .textInputAutocapitalization(.never).autocorrectionDisabled()
                if let fp = host.fingerprint {
                    Section {
                        Text(verbatim: fp).font(.system(.caption, design: .monospaced)).textSelection(.enabled)
                        Button(L("Забыть ключ компьютера"), role: .destructive) { host.fingerprint = nil }
                    } header: {
                        Text(L("Ключ компьютера"))
                    }
                }
                if store.hasPassword(host) {
                    Button(L("Удалить сохранённый пароль"), role: .destructive) { store.deletePassword(host) }
                }
            }
            .navigationTitle(host.name.isEmpty ? L("Компьютер") : host.name)
            .navigationBarTitleDisplayMode(.inline)
            .onAppear { port = String(host.port) }
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button(L("Отмена")) { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button(L("Сохранить")) {
                        host.port = Int(port) ?? 22
                        host.host = host.host.trimmingCharacters(in: .whitespaces)
                        if host.name.isEmpty { host.name = host.host }
                        store.upsert(host)
                        dismiss()
                    }
                    .disabled(host.host.trimmingCharacters(in: .whitespaces).isEmpty || host.user.isEmpty)
                }
            }
        }
    }
}

/// One terminal: signs in (key or password behind Face ID, or the password typed) and shows the shell
final class TerminalSession: NSObject, ObservableObject, TerminalViewDelegate {
    enum State: Equatable { case idle, asking, connecting, open, closed(String?) }

    @Published var state = State.idle
    @Published var note: String?
    let view: SwiftTerm.TerminalView
    private var connection: SSHConnection?
    private(set) var host: SSHHost
    private var usedPassword: String?

    init(host: SSHHost) {
        self.host = host
        view = SwiftTerm.TerminalView(frame: CGRect(x: 0, y: 0, width: 320, height: 480),
                                      font: UIFont.monospacedSystemFont(ofSize: 12, weight: .regular))
        super.init()
        view.terminalDelegate = self
        view.nativeBackgroundColor = UIColor(white: 0.07, alpha: 1)
        view.nativeForegroundColor = UIColor(white: 0.92, alpha: 1)
    }

    /// Key first if this computer has it, then the saved password; else the password is asked
    func start() {
        guard state != .connecting && state != .open else { return }
        let store = SSHStore.shared
        if host.useKey && store.hasKey {
            store.key { [weak self] k in
                guard let self else { return }
                guard let k else { return self.state = .closed(L("Вход отменён.")) }
                self.connect([.key(k)])
            }
        } else if store.hasPassword(host) {
            store.password(host) { [weak self] p in
                guard let self else { return }
                guard let p else { return self.state = .asking }
                self.usedPassword = p
                self.connect([.password(p)])
            }
        } else {
            state = .asking
        }
    }

    func signIn(password: String, save: Bool) {
        usedPassword = password
        if save { SSHStore.shared.savePassword(password, for: host) }
        connect([.password(password)])
    }

    private func connect(_ auth: [SSHConnection.Auth]) {
        state = .connecting
        let c = SSHConnection()
        connection = c
        // Bytes come on the NIO thread; SwiftTerm touches UIKit, so it is fed on the main thread (in order)
        c.onData = { [weak self] bytes in
            DispatchQueue.main.async { self?.view.feed(byteArray: bytes) }
        }
        c.onOpen = { [weak self] in
            DispatchQueue.main.async {
                self?.state = .open
                _ = self?.view.becomeFirstResponder()
            }
        }
        c.onNewHostKey = { [weak self] fp in
            DispatchQueue.main.async {
                guard let self else { return }
                self.host.fingerprint = fp
                SSHStore.shared.upsert(self.host)
                self.note = L("Ключ компьютера запомнен:") + " " + fp
            }
        }
        c.onClose = { [weak self] why in
            DispatchQueue.main.async {
                guard let self else { return }
                // A wrong saved password: ask for it again
                if why != nil, self.state == .connecting, self.usedPassword != nil, !self.host.useKey {
                    self.note = why
                    self.state = .asking
                } else {
                    self.state = .closed(why)
                }
            }
        }
        let t = view.getTerminal()
        c.connect(to: host, auth: auth, cols: t.cols, rows: t.rows)
    }

    func close() {
        connection?.close()
        connection = nil
    }

    /// This iPhone's key into the computer's list (the administrators' one for an administrator, as OpenSSH of Windows reads it)
    func installKey() {
        guard let c = connection else { return }
        note = L("Добавляю ключ на компьютер…")
        let store = SSHStore.shared
        let finish: (String?) -> Void = { [weak self] line in
            guard let self else { return }
            guard let line else { return self.note = nil }
            let script = """
            $k = '\(line)'
            $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
            if ($admin) { $f = Join-Path $env:ProgramData 'ssh\\administrators_authorized_keys' } else { $d = Join-Path $env:USERPROFILE '.ssh'; New-Item -ItemType Directory -Force $d | Out-Null; $f = Join-Path $d 'authorized_keys' }
            if (-not (Test-Path $f) -or -not (Select-String -Path $f -SimpleMatch $k -Quiet)) { Add-Content -Path $f -Value $k -Encoding ascii }
            if ($admin) { icacls $f /inheritance:r /grant '*S-1-5-32-544:F' /grant '*S-1-5-18:F' | Out-Null }
            'litebro-key-ok'
            """
            let encoded = Data(script.utf16.flatMap { [UInt8($0 & 0xff), UInt8($0 >> 8)] }).base64EncodedString()
            c.exec("powershell -NoProfile -NonInteractive -EncodedCommand " + encoded) { out in
                DispatchQueue.main.async {
                    if out?.contains("litebro-key-ok") == true {
                        self.host.useKey = true
                        store.upsert(self.host)
                        self.note = L("Готово: дальше вход по ключу с Face ID. Сохранённый пароль можно удалить в настройках компьютера.")
                    } else {
                        self.note = L("Не удалось добавить ключ на компьютер.")
                    }
                }
            }
        }
        if store.hasKey {
            store.key { k in finish(k.map { SSHStore.publicLine($0) }) }
        } else {
            finish(SSHStore.publicLine(store.makeKey()))
        }
    }

    func send(_ text: String) { connection?.send(Array(text.utf8)) }

    // MARK: TerminalViewDelegate

    func sizeChanged(source: SwiftTerm.TerminalView, newCols: Int, newRows: Int) {
        connection?.resize(cols: newCols, rows: newRows)
    }
    func setTerminalTitle(source: SwiftTerm.TerminalView, title: String) {}
    func hostCurrentDirectoryUpdate(source: SwiftTerm.TerminalView, directory: String?) {}
    func send(source: SwiftTerm.TerminalView, data: ArraySlice<UInt8>) { connection?.send(Array(data)) }
    func scrolled(source: SwiftTerm.TerminalView, position: Double) {}
    func requestOpenLink(source: SwiftTerm.TerminalView, link: String, params: [String: String]) {
        if let u = URL(string: link) { UIApplication.shared.open(u) }
    }
    func bell(source: SwiftTerm.TerminalView) {}
    func clipboardCopy(source: SwiftTerm.TerminalView, content: Data) {
        UIPasteboard.general.string = String(decoding: content, as: UTF8.self)
    }
    func clipboardRead(source: SwiftTerm.TerminalView) -> Data? { UIPasteboard.general.string.map { Data($0.utf8) } }
    func iTermContent(source: SwiftTerm.TerminalView, content: ArraySlice<UInt8>) {}
    func rangeChanged(source: SwiftTerm.TerminalView, startY: Int, endY: Int) {}
}

private struct TerminalHost: UIViewRepresentable {
    let view: SwiftTerm.TerminalView
    func makeUIView(context: Context) -> SwiftTerm.TerminalView { view }
    func updateUIView(_ v: SwiftTerm.TerminalView, context: Context) {}
}

struct TerminalScreen: View {
    @StateObject private var session: TerminalSession
    @State private var password = ""
    @State private var save = true

    init(host: SSHHost) {
        _session = StateObject(wrappedValue: TerminalSession(host: host))
    }

    var body: some View {
        VStack(spacing: 0) {
            if let n = session.note {
                Text(verbatim: n)
                    .font(.footnote)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(8)
                    .background(.thinMaterial)
                    .onTapGesture { session.note = nil }
            }
            ZStack {
                TerminalHost(view: session.view)
                switch session.state {
                case .asking:
                    Form {
                        Section {
                            SecureField(L("Пароль Windows"), text: $password)
                                .textContentType(.password)
                                .onSubmit(go)
                            Toggle(L("Сохранить пароль (Face ID)"), isOn: $save)
                            Button(L("Войти"), action: go).disabled(password.isEmpty)
                        } header: {
                            Text(verbatim: session.host.user + "@" + session.host.host)
                        } footer: {
                            Text(L("Пароль от учётной записи Windows, не ПИН-код. Если вы входите в Windows через учётную запись Microsoft, это её пароль."))
                        }
                    }
                case .connecting, .idle:
                    ProgressView(L("Подключаюсь…")).padding().background(.thinMaterial, in: RoundedRectangle(cornerRadius: 12))
                case .closed(let why):
                    VStack(spacing: 12) {
                        Text(why ?? L("Сеанс завершён.")).multilineTextAlignment(.center)
                        Button(L("Подключиться снова")) { session.start() }.buttonStyle(.borderedProminent)
                    }
                    .padding()
                    .background(.thinMaterial, in: RoundedRectangle(cornerRadius: 12))
                    .padding()
                case .open:
                    EmptyView()
                }
            }
        }
        .background(Color(white: 0.07))
        .navigationTitle(session.host.name)
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Menu {
                    if session.state == .open {
                        Button { session.send("claude\r") } label: { Label(L("Запустить Claude Code"), systemImage: "sparkles") }
                        if !session.host.useKey {
                            Button { session.installKey() } label: { Label(L("Входить по ключу с Face ID"), systemImage: "key") }
                        }
                        Button { if let t = UIPasteboard.general.string { session.send(t) } } label: {
                            Label(L("Вставить"), systemImage: "doc.on.clipboard")
                        }
                    }
                    Button(role: .destructive) { session.close() } label: { Label(L("Отключиться"), systemImage: "xmark") }
                } label: {
                    Image(systemName: "ellipsis.circle")
                }
            }
        }
        .onAppear { session.start() }
        .onDisappear { session.close() }
    }

    private func go() {
        guard !password.isEmpty else { return }
        session.signIn(password: password, save: save)
        password = ""
    }
}
