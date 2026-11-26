import Foundation
import UIKit

/// Taps and swipes from the computer, as the touch screen sends them: IOHIDEvent of IOKit, private API.
/// Works only with the entitlement `com.apple.private.hid.client.event-dispatch` (TrollStore keeps it);
/// without it, or if iOS refuses, nothing happens. Experimental: not tested on a device.
enum Touch {
    private typealias ClientCreate = @convention(c) (CFAllocator?) -> UnsafeMutableRawPointer?
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
    /// backboardd routes a touch by this info: without it a touch event may be dropped
    private static let digitizerInfo: DigitizerInfo? = {
        guard let bks = dlopen("/System/Library/PrivateFrameworks/BackBoardServices.framework/BackBoardServices", RTLD_NOW),
              let p = dlsym(bks, "BKSHIDEventSetDigitizerInfo") else { return nil }
        return unsafeBitCast(p, to: DigitizerInfo.self)
    }()

    private static let iokit = dlopen("/System/Library/Frameworks/IOKit.framework/IOKit", RTLD_NOW)
    private static func fn<T>(_ name: String, _: T.Type) -> T? {
        guard let iokit, let p = dlsym(iokit, name) else { return nil }
        return unsafeBitCast(p, to: T.self)
    }

    private static let createClient = fn("IOHIDEventSystemClientCreate", ClientCreate.self)
    private static let digitizer = fn("IOHIDEventCreateDigitizerEvent", DigitizerEvent.self)
    private static let fingerEvent = fn("IOHIDEventCreateDigitizerFingerEvent", FingerEvent.self)
    private static let keyboard = fn("IOHIDEventCreateKeyboardEvent", KeyboardEvent.self)
    private static let append = fn("IOHIDEventAppendEvent", Append.self)
    private static let setInteger = fn("IOHIDEventSetIntegerValue", SetInteger.self)
    private static let setSender = fn("IOHIDEventSetSenderID", SetSender.self)
    private static let dispatch = fn("IOHIDEventSystemClientDispatchEvent", Dispatch.self)
    private typealias ClientCreateWithType = @convention(c) (CFAllocator?, Int32, CFDictionary?) -> UnsafeMutableRawPointer?
    private static let createSimple = fn("IOHIDEventSystemClientCreateSimpleClient", ClientCreate.self)
    private static let createWithType = fn("IOHIDEventSystemClientCreateWithType", ClientCreateWithType.self)
    /// A client that may send events: the simple client, as tools that tap system-wide use (the plain
    /// IOHIDEventSystemClientCreate one only monitors, and its events went nowhere on the phone)
    private static let made: (UnsafeMutableRawPointer?, String) = {
        if let c = createSimple?(kCFAllocatorDefault) { return (c, "simple") }
        if let c = createWithType?(kCFAllocatorDefault, 1, nil) { return (c, "admin") } // kIOHIDEventSystemClientTypeAdmin
        return (createClient?(kCFAllocatorDefault), "plain")
    }()
    private static var client: UnsafeMutableRawPointer? { made.0 }
    /// Which client was made, for the computer's check (GET _touch)
    static var kind: String { made.1 }

    // IOHIDEventTypes.h
    private static let handType: UInt32 = 3
    private static let rangeBit: UInt32 = 1 << 0, touchBit: UInt32 = 1 << 1, positionBit: UInt32 = 1 << 2
    private static let fieldIsBuiltIn: UInt32 = 4
    private static let fieldDisplayIntegrated: UInt32 = (11 << 16) + 25
    /// The sender of the built-in touch screen, as tools of this kind use
    private static let sender: UInt64 = 0x8000_0008_1731_9372

    static var available: Bool { client != nil && digitizer != nil && fingerEvent != nil && dispatch != nil }

    enum Phase: String { case down, move, up }

    /// A finger at (x, y) of the screen as the phone holds it upright, 0…1 each
    static func finger(_ phase: Phase, x: Double, y: Double) {
        guard let client, let digitizer, let fingerEvent, let append, let dispatch else { return }
        let down = phase != .up
        let mask: UInt32 = phase == .move ? positionBit : (rangeBit | touchBit | (phase == .down ? positionBit : 0))
        let now = mach_absolute_time()
        guard let hand = digitizer(kCFAllocatorDefault, now, handType, 0, 0, mask, 0, x, y, 0, 0, 0,
                                   DarwinBoolean(down), DarwinBoolean(down), 0),
              let one = fingerEvent(kCFAllocatorDefault, now, 1, 2, mask, x, y, 0, down ? 1 : 0, 0,
                               DarwinBoolean(down), DarwinBoolean(down), 0) else { return }
        setInteger?(hand, fieldIsBuiltIn, 1)
        setInteger?(hand, fieldDisplayIntegrated, 1)
        append(hand, one, 0)
        setSender?(hand, sender)
        digitizerInfo?(hand, 0, 0, 0, nil, 0, 0)
        dispatch(client, hand)
        Unmanaged<AnyObject>.fromOpaque(one).release()
        Unmanaged<AnyObject>.fromOpaque(hand).release()
    }

    /// A button: Home (consumer page 0x0C, menu 0x40) or the side button (power 0x30)
    static func button(_ usage: UInt32) {
        guard let client, let keyboard, let dispatch else { return }
        for down in [true, false] {
            guard let e = keyboard(kCFAllocatorDefault, mach_absolute_time(), 0x0C, usage, DarwinBoolean(down), 0) else { continue }
            setSender?(e, sender)
            dispatch(client, e)
            Unmanaged<AnyObject>.fromOpaque(e).release()
            usleep(60_000)
        }
    }
}
