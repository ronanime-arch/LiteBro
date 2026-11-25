import SwiftUI

/// The page console: what the page logged, its errors and requests, and a line to run code in it
struct ConsoleSheet: View {
    @EnvironmentObject var browser: Browser
    @Environment(\.dismiss) private var dismiss
    @ObservedObject var tab: BrowserTab
    @State private var filter = Filter.all
    @State private var code = ""
    @State private var history: [String] = []
    @State private var running = false
    @FocusState private var focused: Bool

    enum Filter: String, CaseIterable, Identifiable {
        case all, errors, warnings, network
        var id: String { rawValue }
        var title: String {
            switch self {
            case .all: return L("Все")
            case .errors: return L("Ошибки")
            case .warnings: return L("Предупреждения")
            case .network: return L("Сеть")
            }
        }
        func has(_ l: ConsoleLine) -> Bool {
            switch self {
            case .all: return true
            case .errors: return l.isError
            case .warnings: return l.kind == .warn
            case .network: return l.kind == .net || l.kind == .netError
            }
        }
    }

    private var lines: [ConsoleLine] { tab.console.filter { filter.has($0) } }

    var body: some View {
        NavigationStack {
            VStack(spacing: 0) {
                Picker(L("Показать"), selection: $filter) {
                    ForEach(Filter.allCases) { f in
                        let n = f == .all ? 0 : tab.console.filter { f.has($0) }.count
                        Text(n > 0 ? f.title + " \(n)" : f.title).tag(f)
                    }
                }
                .pickerStyle(.segmented)
                .padding(8)
                if !Prefs.console {
                    Toggle(L("С начала загрузки каждой страницы"), isOn: Binding(get: { Prefs.console },
                                                                               set: { browser.setConsole($0) }))
                        .font(.footnote)
                        .padding(.horizontal, 12)
                        .padding(.bottom, 6)
                }
                ScrollViewReader { proxy in
                    List {
                        if lines.isEmpty {
                            Text(Prefs.console ? L("Пока пусто.") : L("Пока пусто. Сообщения страницы видны с момента открытия консоли; чтобы видеть их с начала загрузки, включите переключатель выше и обновите страницу."))
                                .font(.footnote)
                                .foregroundStyle(.secondary)
                        }
                        ForEach(lines) { l in
                            row(l).id(l.id)
                        }
                    }
                    .listStyle(.plain)
                    .onChange(of: tab.console.count) { _ in
                        if let last = lines.last { withAnimation { proxy.scrollTo(last.id, anchor: .bottom) } }
                    }
                    .onAppear { if let last = lines.last { proxy.scrollTo(last.id, anchor: .bottom) } }
                }
                Divider()
                HStack(alignment: .bottom, spacing: 8) {
                    Text(verbatim: "›").font(.system(.body, design: .monospaced)).foregroundStyle(.blue)
                        .padding(.bottom, 6)
                    TextField(L("Код JavaScript"), text: $code, axis: .vertical)
                        .font(.system(.footnote, design: .monospaced))
                        .lineLimit(1...6)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .focused($focused)
                        .padding(6)
                        .background(Color(.secondarySystemBackground), in: RoundedRectangle(cornerRadius: 8))
                    if !history.isEmpty {
                        Menu {
                            ForEach(Array(history.reversed().enumerated()), id: \.offset) { item in
                                Button(item.element) { code = item.element }
                            }
                        } label: {
                            Image(systemName: "clock.arrow.circlepath").padding(.bottom, 6)
                        }
                        .accessibilityLabel(L("Недавние"))
                    }
                    Button(action: run) {
                        if running { ProgressView() } else { Image(systemName: "play.fill") }
                    }
                    .padding(.bottom, 6)
                    .disabled(code.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty || running || tab.webView == nil)
                    .accessibilityLabel(L("Выполнить"))
                }
                .padding(8)
            }
            .navigationTitle(L("Консоль страницы"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Menu {
                        Button { UIPasteboard.general.string = lines.map(\.text).joined(separator: "\n") } label: {
                            Label(L("Копировать всё"), systemImage: "doc.on.doc")
                        }
                        Button(role: .destructive) { tab.console.removeAll() } label: {
                            Label(L("Очистить"), systemImage: "trash")
                        }
                    } label: {
                        Image(systemName: "ellipsis.circle")
                    }
                }
                ToolbarItem(placement: .confirmationAction) { Button(L("Готово")) { dismiss() } }
            }
            .onAppear { if !Prefs.console { tab.startConsole() } }
        }
    }

    private func row(_ l: ConsoleLine) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 6) {
            Image(systemName: icon(l.kind))
                .font(.caption2)
                .foregroundStyle(color(l.kind))
                .frame(width: 14)
            Text(verbatim: l.text)
                .font(.system(.caption, design: .monospaced))
                .foregroundStyle(l.kind == .input || l.kind == .debug ? Color.secondary : color(l.kind))
                .textSelection(.enabled)
                .lineLimit(40)
        }
        .listRowInsets(EdgeInsets(top: 4, leading: 10, bottom: 4, trailing: 10))
        .listRowBackground(l.isError ? Color.red.opacity(0.08) : l.kind == .warn ? Color.yellow.opacity(0.08) : Color.clear)
        .contextMenu {
            Button { UIPasteboard.general.string = l.text } label: { Label(L("Копировать"), systemImage: "doc.on.doc") }
            if l.kind == .input {
                Button { code = l.text } label: { Label(L("Изменить и выполнить снова"), systemImage: "pencil") }
            }
        }
    }

    private func icon(_ k: ConsoleLine.Kind) -> String {
        switch k {
        case .error, .netError: return "xmark.octagon.fill"
        case .warn: return "exclamationmark.triangle.fill"
        case .info: return "info.circle.fill"
        case .net: return "arrow.up.arrow.down"
        case .input: return "chevron.right"
        case .result: return "arrow.turn.down.left"
        case .log, .debug: return "circle.fill"
        }
    }

    private func color(_ k: ConsoleLine.Kind) -> Color {
        switch k {
        case .error, .netError: return .red
        case .warn: return .orange
        case .info, .result: return .blue
        case .net: return Color.secondary
        default: return .primary
        }
    }

    private func run() {
        let c = code.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !c.isEmpty, let w = tab.webView else { return }
        history.removeAll { $0 == c }
        history.append(c)
        if history.count > 20 { history.removeFirst() }
        tab.log([ConsoleLine(kind: .input, text: c)])
        code = ""
        running = true
        filter = .all
        PageConsole.run(c, in: w) { text, failed in
            running = false
            tab.log([ConsoleLine(kind: failed ? .error : .result, text: text)])
        }
    }
}
