import SwiftUI
import UIKit

/// «Поделиться» without SwiftUI's ShareLink. On iOS 16 ShareLink's sheet crashes the app when the view under it
/// redraws (a timer, a state change): the share sheet's collection view outlives its data source. Here UIKit
/// presents UIActivityViewController from the top view controller, so a redraw of the SwiftUI view does not touch it.
struct ShareButton: View {
    let item: URL
    var title = L("Поделиться")

    var body: some View {
        Button { Share.present([item]) } label: { Label(title, systemImage: "square.and.arrow.up") }
    }
}

enum Share {
    static func present(_ items: [Any]) {
        // After a menu or a sheet's own animation: presenting on top of a closing menu is refused
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) {
            guard let top = topViewController() else { return }
            let sheet = UIActivityViewController(activityItems: items, applicationActivities: nil)
            // Without AirDrop: its row draws its icon in LiteBro's own process through CoreImage, and there
            // CoreImage takes its OpenGL way and calls a missing function: the app died every time the sheet
            // opened (10 such crashes in the user's log by 2026-11-27, CI::GLContext under
            // -[UIAirDropActivity _activityImage]). The other rows are drawn by the system's own process.
            sheet.excludedActivityTypes = [.airDrop]
            if let pop = sheet.popoverPresentationController { // iPad: from the middle of the screen
                pop.sourceView = top.view
                pop.sourceRect = CGRect(x: top.view.bounds.midX, y: top.view.bounds.midY, width: 0, height: 0)
                pop.permittedArrowDirections = []
            }
            top.present(sheet, animated: true)
        }
    }

    private static func topViewController() -> UIViewController? {
        let window = UIApplication.shared.connectedScenes
            .compactMap { $0 as? UIWindowScene }
            .flatMap(\.windows)
            .first { $0.isKeyWindow }
        var top = window?.rootViewController
        while let next = top?.presentedViewController, !next.isBeingDismissed { top = next }
        return top
    }
}
