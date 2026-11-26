import SwiftUI

/// The launch log of the debug module: to read, copy and send
struct DebugLogView: View {
    @State private var text = Debug.text()
    @State private var copied = false
    @State private var asked = Debug.safeMode

    var body: some View {
        ScrollView {
            Text(verbatim: text.isEmpty ? "—" : text)
                .font(.system(size: 11, design: .monospaced))
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(10)
        }
        .defaultScrollAnchorBottom()
        .navigationTitle(L("Журнал отладки"))
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItemGroup(placement: .bottomBar) {
                ShareLink(item: Debug.logFile) { Label(L("Отправить"), systemImage: "square.and.arrow.up") }
                Spacer()
                Button { UIPasteboard.general.string = text; copied = true } label: {
                    Label(copied ? L("Скопировано") : L("Копировать"), systemImage: "doc.on.doc")
                }
                Spacer()
                Button(role: .destructive) { Debug.clear(); text = Debug.text() } label: {
                    Label(L("Очистить"), systemImage: "trash")
                }
            }
        }
        .onAppear { text = Debug.text() }
    }
}

/// The notice on the start page while the safe mode is on
struct SafeModeNotice: View {
    @Binding var sheet: Sheet?

    var body: some View {
        if Debug.safeMode {
            VStack(alignment: .leading, spacing: 6) {
                Label(L("Безопасный режим"), systemImage: "stethoscope").font(.headline)
                Text(verbatim: L("Прошлый запуск не дошёл до конца, поэтому сейчас LiteBro открылся без сохранённых вкладок, списков фильтров и правил блокировки. Следующий запуск — обычный.")
                     + (Debug.failedStep.map { "\n" + L("Последний шаг:") + " " + $0 } ?? ""))
                    .font(.footnote).foregroundStyle(.secondary)
                Button(L("Журнал отладки")) { sheet = .debug }.font(.footnote.weight(.semibold))
            }
            .padding(12)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(RoundedRectangle(cornerRadius: 12).fill(Color.orange.opacity(0.18)))
            .padding(.horizontal)
        }
    }
}

private extension View {
    /// The newest lines at the end are shown first (iOS 17); on iOS 16 the log opens at its top
    @ViewBuilder func defaultScrollAnchorBottom() -> some View {
        if #available(iOS 17.0, *) { self.defaultScrollAnchor(.bottom) } else { self }
    }
}
