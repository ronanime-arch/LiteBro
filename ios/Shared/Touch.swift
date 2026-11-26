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
        }
    }

    /// A finger at (x, y) of the screen as the phone holds it upright, 0…1 each
    static func finger(_ phase: Phase, x: Double, y: Double, _ o: Options = Options()) {
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
