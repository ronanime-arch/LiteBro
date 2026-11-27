import Foundation
import UIKit

/// Taps and swipes from the computer, as the touch screen sends them: IOHIDEvent of IOKit, private API.
/// Works only with the entitlement `com.apple.private.hid.client.event-dispatch` (TrollStore keeps it).
/// On the user's iPhone (iOS 16.2) the Home button gets through and a finger did not: how a finger event is
/// made and sent can be chosen per request (`Touch.Options`), so the working way is found from the computer.
enum Touch {
    private typealias ClientCreate = @convention(c) (CFAllocator?) -> UnsafeMutableRawPointer?
    private typealias ClientCreateWithType = @convention(c) (CFAllocator?, Int32, CFDictionary?) -> UnsafeMutableRawPointer?
    private typealias DigitizerEvent = @convention(c) (CFAllocator?, UInt64, UInt32, UInt32, UInt32, UInt32, UInt32,
                                                       Double, Double, Double, Double, Double, DarwinBoolean, DarwinBoolean, UInt32) -> UnsafeMutableRawPointer?
    private typealias FingerEvent = @convention(c) (CFAllocator?, UInt64, UInt32, UInt32, UInt32,
                                                    Double, Double, Double, Double, Double, DarwinBoolean, DarwinBoolean, UInt32) -> UnsafeMutableRawPointer?
    private typealias KeyboardEvent = @convention(c) (CFAllocator?, UInt64, UInt32, UInt32, DarwinBoolean, UInt32) -> UnsafeMutableRawPointer?
    private typealias Append = @convention(c) (UnsafeMutableRawPointer, UnsafeMutableRawPointer, UInt32) -> Void
    private typealias SetInteger = @convention(c) (UnsafeMutableRawPointer, UInt32, Int) -> Void
    private typealias SetSender = @convention(c) (UnsafeMutableRawPointer, UInt64) -> Void
    private typealias Dispatch = @convention(c) (UnsafeMutableRawPointer, UnsafeMutableRawPointer) -> Void
    private typealias DigitizerInfo = @convention(c) (UnsafeMutableRawPointer, UInt32, UInt8, UInt8, CFString?, Double, Float) -> Void

    private static let iokit = dlopen("/System/Library/Frameworks/IOKit.framework/IOKit", RTLD_NOW)
    private static func fn<T>(_ name: String, _: T.Type) -> T? {
        guard let iokit, let p = dlsym(iokit, name) else { return nil }
        return unsafeBitCast(p, to: T.self)
    }

    private static let createPlain = fn("IOHIDEventSystemClientCreate", ClientCreate.self)
    private static let createSimple = fn("IOHIDEventSystemClientCreateSimpleClient", ClientCreate.self)
    private static let createWithType = fn("IOHIDEventSystemClientCreateWithType", ClientCreateWithType.self)
    private static let digitizer = fn("IOHIDEventCreateDigitizerEvent", DigitizerEvent.self)
    private static let fingerEvent = fn("IOHIDEventCreateDigitizerFingerEvent", FingerEvent.self)
    private static let keyboard = fn("IOHIDEventCreateKeyboardEvent", KeyboardEvent.self)
    private static let append = fn("IOHIDEventAppendEvent", Append.self)
    private static let setInteger = fn("IOHIDEventSetIntegerValue", SetInteger.self)
    private static let setSender = fn("IOHIDEventSetSenderID", SetSender.self)
    private static let dispatch = fn("IOHIDEventSystemClientDispatchEvent", Dispatch.self)
    /// backboardd routes a touch by this info
    private static let digitizerInfo: DigitizerInfo? = {
        guard let bks = dlopen("/System/Library/PrivateFrameworks/BackBoardServices.framework/BackBoardServices", RTLD_NOW),
              let p = dlsym(bks, "BKSHIDEventSetDigitizerInfo") else { return nil }
        return unsafeBitCast(p, to: DigitizerInfo.self)
    }()

    /// The clients, made once each: "plain" (the one the Home button works with), "simple", "admin"
    private static var clients: [String: UnsafeMutableRawPointer] = [:]
    private static let lock = NSLock()
    private static func client(_ kind: String) -> UnsafeMutableRawPointer? {
        lock.lock(); defer { lock.unlock() }
        if let c = clients[kind] { return c }
        let made: UnsafeMutableRawPointer?
        switch kind {
        case "simple": made = createSimple?(kCFAllocatorDefault)
        case "admin": made = createWithType?(kCFAllocatorDefault, 1, nil)
        default: made = createPlain?(kCFAllocatorDefault)
        }
        if let made { clients[kind] = made }
        return made
    }

    /// What can be made, for the computer's check
    static var report: String {
        "{\"available\":\(available),\"simple\":\(createSimple != nil),\"withType\":\(createWithType != nil),\"bks\":\(digitizerInfo != nil)}"
    }

    // IOHIDEventTypes.h
    private static let handType: UInt32 = 3
    private static let rangeBit: UInt32 = 1 << 0, touchBit: UInt32 = 1 << 1, positionBit: UInt32 = 1 << 2
    private static let fieldIsBuiltIn: UInt32 = 4
    private static let fieldDisplayIntegrated: UInt32 = (11 << 16) + 25
    private static let builtInSender: UInt64 = 0x0000_0001_0000_027F

    static var available: Bool { createPlain != nil && digitizer != nil && fingerEvent != nil && dispatch != nil }

    enum Phase: String { case down, move, up }

    /// How a finger event is made and sent; every key is optional in the request's JSON
    struct Options {
        var client = "plain"
        var sender = Touch.builtInSender
        var bks = true
        var points = false      // x, y in points of the screen instead of 0…1
        var parentXY = true     // the hand event carries the point too
        var index: UInt32 = 1, identity: UInt32 = 2
        var builtIn = true
        var pressure = 1.0
        /// Into this app only, as Apple's UI tests do (UIApplication's own HID queue): no rights needed,
        /// and it works where system-wide taps do not; x, y become points of the window
        var inApp = false

        init() {}
        init(_ m: [String: Any]) {
            if let v = m["client"] as? String { client = v }
            if let v = m["sender"] as? String, let n = UInt64(v.replacingOccurrences(of: "0x", with: ""), radix: 16) { sender = n }
            if let v = m["bks"] as? Bool { bks = v }
            if let v = m["points"] as? Bool { points = v }
            if let v = m["parentXY"] as? Bool { parentXY = v }
            if let v = m["index"] as? Int { index = UInt32(v) }
            if let v = m["identity"] as? Int { identity = UInt32(v) }
            if let v = m["builtIn"] as? Bool { builtIn = v }
            if let v = m["pressure"] as? Double { pressure = v }
            if let v = m["inApp"] as? Bool { inApp = v; if v && m["points"] == nil { points = true } }
        }
    }

    /// A finger at (x, y) of the screen as the phone holds it upright, 0…1 each
    static func finger(_ phase: Phase, x: Double, y: Double, _ o: Options = Options()) {
        if o.inApp {
            // On the main thread: the event is made, given the key window's context and queued in UIKit
            DispatchQueue.main.async { _ = fingerInApp(phase, x: x, y: y, o) }
            return
        }
        guard let client = client(o.client), let digitizer, let fingerEvent, let append, let dispatch else { return }
        var px = x, py = y
        if o.points {
            let size = UIScreen.main.bounds.size
            px = x * Double(size.width); py = y * Double(size.height)
        }
        let down = phase != .up
        let mask: UInt32 = phase == .move ? positionBit : (rangeBit | touchBit | (phase == .down ? positionBit : 0))
        let now = mach_absolute_time()
        guard let hand = digitizer(kCFAllocatorDefault, now, handType, 0, 0, mask, 0, o.parentXY ? px : 0, o.parentXY ? py : 0, 0, 0, 0,
                                   DarwinBoolean(down), DarwinBoolean(down), 0),
              let one = fingerEvent(kCFAllocatorDefault, now, o.index, o.identity, mask, px, py, 0, down ? o.pressure : 0, 0,
                                    DarwinBoolean(down), DarwinBoolean(down), 0) else { return }
        if o.builtIn { setInteger?(hand, fieldIsBuiltIn, 1) }
        setInteger?(hand, fieldDisplayIntegrated, 1)
        append(hand, one, 0)
        setSender?(hand, o.sender)
        if o.bks { digitizerInfo?(hand, 0, 0, 0, nil, 0, 0) }
        dispatch(client, hand)
        Unmanaged<AnyObject>.fromOpaque(one).release()
        Unmanaged<AnyObject>.fromOpaque(hand).release()
    }

    /// The key window of this app, looked up by name: the broadcast extension has no UIApplication.shared
    private static func keyWindow() -> (NSObject, UIWindow?)? {
        guard let appClass = NSClassFromString("UIApplication") as? NSObject.Type,
              let app = appClass.value(forKey: "sharedApplication") as? NSObject else { return nil }
        let scenes = app.value(forKey: "connectedScenes") as? Set<UIScene> ?? []
        let window = scenes.compactMap { $0 as? UIWindowScene }.flatMap { $0.windows }.first { $0.isKeyWindow }
        return (app, window)
    }

    /// The in-app way (WebKit's HIDEventGenerator does the same): UIApplication._enqueueHIDEvent:, with the
    /// digitizer info carrying the key window's context id. On the main thread; returns what happened as JSON,
    /// so the computer sees why a tap did nothing (the request used to answer 204 either way).
    static func fingerInApp(_ phase: Phase, x: Double, y: Double, _ o: Options) -> String {
        guard let digitizer, let fingerEvent, let append else { return "{\"error\":\"no IOHIDEvent functions\"}" }
        guard let kw = keyWindow() else { return "{\"error\":\"no UIApplication\"}" }
        let app = kw.0, window = kw.1
        let size = window?.bounds.size ?? UIScreen.main.bounds.size
        let px = o.points ? x * Double(size.width) : x, py = o.points ? y * Double(size.height) : y
        let down = phase != .up
        let mask: UInt32 = phase == .move ? positionBit : (rangeBit | touchBit | (phase == .down ? positionBit : 0))
        let now = mach_absolute_time()
        guard let hand = digitizer(kCFAllocatorDefault, now, handType, 0, 0, mask, 0, 0, 0, 0, 0, 0,
                                   DarwinBoolean(down), DarwinBoolean(down), 0),
              let one = fingerEvent(kCFAllocatorDefault, now, o.index, o.identity, mask, px, py, 0, down ? o.pressure : 0, 0,
                                    DarwinBoolean(down), DarwinBoolean(down), 0) else { return "{\"error\":\"event not made\"}" }
        setInteger?(hand, fieldDisplayIntegrated, 1)
        append(hand, one, 0)
        // As the system path does: UIKit may drop an event without a sender
        setSender?(hand, o.sender)
        let context = (window?.value(forKey: "_contextId") as? NSNumber)?.uint32Value ?? 0
        if o.bks { digitizerInfo?(hand, context, 0, 0, nil, 0, 0) }
        let event = Unmanaged<AnyObject>.fromOpaque(hand).takeUnretainedValue()
        let enqueue = NSSelectorFromString("_enqueueHIDEvent:")
        let responds = app.responds(to: enqueue)
        if responds { _ = app.perform(enqueue, with: event) }
        Unmanaged<AnyObject>.fromOpaque(one).release()
        Unmanaged<AnyObject>.fromOpaque(hand).release()
        let hit = window?.hitTest(CGPoint(x: px, y: py), with: nil).map { String(describing: type(of: $0)) } ?? ""
        return "{\"window\":\(window != nil),\"key\":\(window?.isKeyWindow ?? false),\"context\":\(context),"
            + "\"enqueue\":\(responds),\"x\":\(px),\"y\":\(py),\"hit\":\"\(hit)\"}"
    }

    /// A tap without the HID queue, for UIKit controls (bar buttons such as «Готово», switches, buttons):
    /// the control under the point gets its actions as if a finger lifted on it. SwiftUI rows are not controls.
    /// x, y are 0…1 of the window; on the main thread; returns what was found as JSON.
    static func click(x: Double, y: Double) -> String {
        guard let window = keyWindow()?.1 else { return "{\"error\":\"no key window\"}" }
        let p = CGPoint(x: x * Double(window.bounds.width), y: y * Double(window.bounds.height))
        var view = window.hitTest(p, with: nil)
        let hit = view.map { String(describing: type(of: $0)) } ?? ""
        while let v = view, !(v is UIControl) { view = v.superview }
        guard let control = view as? UIControl else { return "{\"hit\":\"\(hit)\",\"control\":null}" }
        if let s = control as? UISwitch { s.setOn(!s.isOn, animated: true); s.sendActions(for: .valueChanged) }
        else { control.sendActions(for: .touchUpInside); control.sendActions(for: .primaryActionTriggered) }
        return "{\"hit\":\"\(hit)\",\"control\":\"\(String(describing: type(of: control)))\"}"
    }

    /// Text into whatever has the focus in this app (a field of LiteBro or of a page); "\b" deletes one character
    static func type(_ text: String) {
        DispatchQueue.main.async {
            guard let kw = keyWindow(),
                  let responder = kw.1?.value(forKey: "firstResponder") as? UIKeyInput else { return }
            if text == "\u{8}" { responder.deleteBackward() } else { responder.insertText(text) }
        }
    }

    /// A button: Home (consumer page 0x0C, menu 0x40) or the side button (power 0x30)
    static func button(_ usage: UInt32) {
        guard let client = client("plain"), let keyboard, let dispatch else { return }
        for down in [true, false] {
            guard let e = keyboard(kCFAllocatorDefault, mach_absolute_time(), 0x0C, usage, DarwinBoolean(down), 0) else { continue }
            setSender?(e, builtInSender)
            dispatch(client, e)
            Unmanaged<AnyObject>.fromOpaque(e).release()
            usleep(60_000)
        }
    }
}
