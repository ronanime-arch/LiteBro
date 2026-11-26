import Darwin
import Foundation
import UIKit

/// The debug module, kept until the app is ready for release: a launch log with memory, crash traces
/// (signals and Objective-C exceptions) and a safe mode after a launch that did not finish.
/// The log is written straight to the file descriptor, so it survives a kill by the system, and it lies in
/// Files › On My iPhone › LiteBro › Debug (UIFileSharingEnabled), readable without starting the app.
enum Debug {
    static let dir: URL = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
        .appendingPathComponent("Debug", isDirectory: true)
    static var logFile: URL { dir.appendingPathComponent("log.txt") }
    /// Exists while a launch is under way; left behind by a launch that died before the first screen
    private static var launching: URL { dir.appendingPathComponent("launching.txt") }
    /// The whole phone build also mirrors the log where Filza and the PC see it without the app container
    private static let mirror = URL(fileURLWithPath: "/var/mobile/Media/LiteBro-Debug", isDirectory: true)
    private static let forceSafeKey = "debugSafeNext"

    /// This launch skips the saved tabs, the filter lists and the rules, because the last one did not finish
    private(set) static var safeMode = false
    /// The last step of the launch that did not finish, for the notice
    private(set) static var failedStep: String?
    private static var started = false
    private static var start = Date()

    /// The very first call of the app
    static func begin() {
        guard !started else { return }
        started = true
        start = Date()
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        rotate()
        openLogs()
        installCrashHandlers()

        let forced = UserDefaults.standard.bool(forKey: forceSafeKey)
        UserDefaults.standard.removeObject(forKey: forceSafeKey)
        if let last = try? String(contentsOf: launching, encoding: .utf8) {
            failedStep = last.trimmingCharacters(in: .whitespacesAndNewlines)
            safeMode = true
        }
        if forced { safeMode = true }

        let info = Bundle.main.infoDictionary ?? [:]
        let os = ProcessInfo.processInfo.operatingSystemVersion
        write("")
        write("===== launch \(Date()) =====")
        write("LiteBro \(info["CFBundleShortVersionString"] as? String ?? "?") build \(build), iOS \(os.majorVersion).\(os.minorVersion).\(os.patchVersion), \(model())")
        write("bundle \(Bundle.main.bundlePath)")
        write("data \(NSHomeDirectory())")
        write("sandbox: \(FileManager.default.isReadableFile(atPath: "/var/mobile/Media") ? "no (whole phone)" : "yes")"
              + ", free disk \(freeDiskMB()) MB")
        if let s = failedStep { write("!! the last launch did not finish, its last step: \(s)") }
        if let c = lastCrash() { write("!! crash of an earlier run:\n" + c) }
        if safeMode { write("SAFE MODE: no saved tabs, no filter lists, no rules" + (forced ? " (asked in the settings)" : "")) }
        step("debug ready")

        let nc = NotificationCenter.default
        nc.addObserver(forName: UIApplication.didReceiveMemoryWarningNotification, object: nil, queue: nil) { _ in
            write("memory warning, \(memoryMB()) MB")
        }
        nc.addObserver(forName: UIApplication.didEnterBackgroundNotification, object: nil, queue: nil) { _ in step("background") }
        nc.addObserver(forName: UIApplication.willEnterForegroundNotification, object: nil, queue: nil) { _ in step("foreground") }
        nc.addObserver(forName: UIApplication.willTerminateNotification, object: nil, queue: nil) { _ in step("terminate") }
    }

    /// A launch step: the line with the time since the start and the memory, and the step kept in launching.txt
    static func step(_ s: String) {
        let t = String(format: "%.2f", Date().timeIntervalSince(start))
        write("[\(t)s \(memoryMB()) MB] \(s)")
        if !ready { try? s.write(to: launching, atomically: false, encoding: .utf8) }
    }

    private(set) static var ready = false

    /// The commit the build was made from (build.txt, written by the workflow)
    static let build: String = Bundle.main.url(forResource: "build", withExtension: "txt")
        .flatMap { try? String(contentsOf: $0, encoding: .utf8) }?
        .trimmingCharacters(in: .whitespacesAndNewlines) ?? "dev"

    /// The first screen has been up for a while: the launch worked, the next one is normal again
    static func launched() {
        guard !ready else { return }
        DispatchQueue.main.asyncAfter(deadline: .now() + 5) {
            guard !ready else { return }
            step("launch finished")
            ready = true
            try? FileManager.default.removeItem(at: launching)
        }
    }

    /// Long work that could get the app killed: the next launches skip it once it died in the middle
    static func guarded<T>(_ name: String, _ work: () -> T) -> T {
        mark(name)
        let r = work()
        unmark(name)
        return r
    }

    /// The same for work that ends in a callback
    static func mark(_ name: String) {
        step("\(name) …")
        try? Data().write(to: dir.appendingPathComponent("busy-\(name).txt"))
    }

    static func unmark(_ name: String) {
        try? FileManager.default.removeItem(at: dir.appendingPathComponent("busy-\(name).txt"))
        step("\(name) done")
    }

    /// Did an earlier run die inside this work (it stays skipped until the settings run it by hand)
    static func diedIn(_ name: String) -> Bool {
        FileManager.default.fileExists(atPath: dir.appendingPathComponent("busy-\(name).txt").path)
    }

    /// Not in the safe mode, and not work an earlier run died in
    static func allowed(_ name: String) -> Bool {
        if safeMode { return false }
        if diedIn(name) {
            write("skipped \(name): an earlier run died in it (Settings › Update lists runs it again)")
            return false
        }
        return true
    }

    static func forget(_ name: String) {
        try? FileManager.default.removeItem(at: dir.appendingPathComponent("busy-\(name).txt"))
    }

    static func safeNextLaunch() { UserDefaults.standard.set(true, forKey: forceSafeKey); write("safe mode asked for the next launch") }

    static func text() -> String { (try? String(contentsOf: logFile, encoding: .utf8)) ?? "" }

    static func clear() {
        for fd in fds { ftruncate(fd, 0) }
        try? FileManager.default.removeItem(at: dir.appendingPathComponent("log.1.txt"))
        try? FileManager.default.removeItem(at: dir.appendingPathComponent("crash.txt"))
        write("log cleared \(Date())")
    }

    // MARK: Writing

    static func write(_ line: String) {
        NSLog("LiteBro: %@", line)
        let d = Array((line + "\n").utf8)
        lock.lock()
        for fd in fds { _ = d.withUnsafeBytes { Darwin.write(fd, $0.baseAddress, $0.count) } }
        lock.unlock()
    }

    private static let lock = NSLock()
    private static var fds: [Int32] = []

    private static func openLogs() {
        var paths = [logFile.path]
        if (try? FileManager.default.createDirectory(at: mirror, withIntermediateDirectories: true)) != nil {
            paths.append(mirror.appendingPathComponent("log.txt").path)
        }
        fds = paths.map { open($0, O_WRONLY | O_CREAT | O_APPEND, 0o644) }.filter { $0 >= 0 }
        crashFDs = fds
    }

    /// The log keeps the last 512 KB and one older file
    private static func rotate() {
        let size = (try? FileManager.default.attributesOfItem(atPath: logFile.path)[.size] as? Int) ?? 0
        guard size > 512 * 1024 else { return }
        let old = dir.appendingPathComponent("log.1.txt")
        try? FileManager.default.removeItem(at: old)
        try? FileManager.default.moveItem(at: logFile, to: old)
    }

    // MARK: Crashes

    private static func installCrashHandlers() {
        crashFile = open(dir.appendingPathComponent("crash.txt").path, O_WRONLY | O_CREAT | O_APPEND, 0o644)
        for s in [SIGABRT, SIGSEGV, SIGBUS, SIGILL, SIGTRAP, SIGFPE, SIGSYS] { signal(s, onSignal) }
        NSSetUncaughtExceptionHandler { e in
            let text = "Objective-C exception \(e.name.rawValue): \(e.reason ?? "")\n"
                + e.callStackSymbols.joined(separator: "\n") + "\n"
            let d = Array(text.utf8)
            for fd in crashFDs + [crashFile] { _ = d.withUnsafeBytes { Darwin.write(fd, $0.baseAddress, $0.count) } }
        }
    }

    /// The crash of an earlier run, read once and then kept only in the log
    private static func lastCrash() -> String? {
        let f = dir.appendingPathComponent("crash.txt")
        guard let s = try? String(contentsOf: f, encoding: .utf8), !s.isEmpty else { return nil }
        try? FileManager.default.removeItem(at: f)
        crashFile = open(f.path, O_WRONLY | O_CREAT | O_APPEND, 0o644)
        return s
    }

    // MARK: Device

    static func memoryMB() -> Int {
        var info = task_vm_info_data_t()
        var count = mach_msg_type_number_t(MemoryLayout<task_vm_info_data_t>.size / MemoryLayout<integer_t>.size)
        let r = withUnsafeMutablePointer(to: &info) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                task_info(mach_task_self_, task_flavor_t(TASK_VM_INFO), $0, &count)
            }
        }
        return r == KERN_SUCCESS ? Int(info.phys_footprint / 1_048_576) : -1
    }

    private static func freeDiskMB() -> Int {
        let v = try? URL(fileURLWithPath: NSHomeDirectory()).resourceValues(forKeys: [.volumeAvailableCapacityKey])
        return (v?.volumeAvailableCapacity ?? 0) / 1_048_576
    }

    private static func model() -> String {
        var u = utsname()
        uname(&u)
        return withUnsafeBytes(of: &u.machine) { String(decoding: $0.prefix { $0 != 0 }, as: UTF8.self) }
    }
}

// Globals, so the C signal handler reaches them without a context
private var crashFDs: [Int32] = []
private var crashFile: Int32 = -1

/// Writes the signal and the stack to the log and crash.txt, then lets the system end the app as usual
private func onSignal(_ sig: Int32) {
    var head: [UInt8] = Array("!! CRASH signal ".utf8)
    var n = sig
    var digits: [UInt8] = []
    repeat { digits.insert(UInt8(48 + n % 10), at: 0); n /= 10 } while n > 0
    head += digits + [10]
    var frames = [UnsafeMutableRawPointer?](repeating: nil, count: 64)
    let count = backtrace(&frames, 64)
    for fd in crashFDs + [crashFile] where fd >= 0 {
        _ = head.withUnsafeBytes { write(fd, $0.baseAddress, $0.count) }
        backtrace_symbols_fd(&frames, count, fd)
    }
    signal(sig, SIG_DFL)
    raise(sig)
}
