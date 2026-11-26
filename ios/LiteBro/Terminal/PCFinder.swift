import CryptoKit
import Darwin
import Foundation
import UIKit

/// Finds LiteBro on the computers of this Wi-Fi (UDP broadcast, port 47321, PhoneFind in Phone.cs on Windows)
/// and asks one to let this iPhone in: the person answers on the computer, and the computer adds this iPhone's
/// key for SSH. No QR code and no Windows password.
final class PCFinder: ObservableObject {
    struct Found: Identifiable, Hashable {
        let ip: String
        let name: String
        let user: String
        var id: String { ip + "|" + user }
    }

    enum Pairing: Equatable {
        case asking(code: String)   // waiting for the person at the computer
        case done(SSHHost)
        case failed(String)
    }

    static let port: UInt16 = 47321

    @Published private(set) var found: [Found] = []
    @Published private(set) var searching = false
    @Published var pairing: [String: Pairing] = [:]   // by Found.id

    private let queue = DispatchQueue(label: "litebro.pcfinder")

    // MARK: Finding

    func search() {
        guard !searching else { return }
        searching = true
        queue.async { [weak self] in
            var list: [Found] = []
            let replies = Self.exchange(["litebro": 1, "type": "find"], to: Self.broadcasts(), tries: 3, wait: 0.7)
            for (ip, m) in replies where m["type"] as? String == "here" {
                let f = Found(ip: ip, name: m["name"] as? String ?? ip, user: m["user"] as? String ?? "")
                if !f.user.isEmpty, !list.contains(f) { list.append(f) }
            }
            DispatchQueue.main.async {
                self?.found = list.sorted { $0.name < $1.name }
                self?.searching = false
            }
        }
    }

    // MARK: Pairing

    func pair(_ f: Found) {
        let store = SSHStore.shared
        store.publicKeyLine { [weak self] line in
            guard let self else { return }
            guard let line else { return self.pairing[f.id] = nil }
            let code = Self.code(line)
            self.pairing[f.id] = .asking(code: code)
            let id = UUID().uuidString
            let ask: [String: Any] = ["litebro": 1, "type": "pair", "id": id, "key": line, "phone": UIDevice.current.name]
            self.queue.async {
                // The person may be away from the computer: asked again every 1.5 s for three minutes
                let deadline = Date().addingTimeInterval(180)
                var result: Pairing = .failed(L("Компьютер не ответил. Он включён, LiteBro на нём запущен и в той же сети Wi-Fi?"))
                loop: while Date() < deadline {
                    let replies = Self.exchange(ask, to: [f.ip], tries: 1, wait: 1.5)
                    guard let m = replies.first(where: { $0.1["type"] as? String == "pair" && $0.1["id"] as? String == id })?.1 else { continue }
                    switch m["state"] as? String {
                    case "pending":
                        continue
                    case "ok":
                        let fp = (m["fp"] as? String).flatMap { $0.hasPrefix("SHA256:") ? $0 : nil }
                        var h = store.hosts.first { $0.host == f.ip && $0.user == (m["user"] as? String ?? f.user) }
                            ?? SSHHost(name: m["name"] as? String ?? f.name, host: f.ip, user: m["user"] as? String ?? f.user)
                        h.port = m["port"] as? Int ?? 22
                        h.useKey = true
                        if let fp { h.fingerprint = fp }
                        result = .done(h)
                    case "denied":
                        result = .failed(L("На компьютере отказали."))
                    case "busy":
                        result = .failed(L("Компьютер уже спрашивает про другой телефон. Попробуйте через минуту."))
                    default:
                        result = .failed(L("Компьютер не смог добавить ключ: Windows не дала прав администратора или не смогла включить вход по SSH."))
                    }
                    break loop
                }
                DispatchQueue.main.async {
                    if case .done(let h) = result { store.upsert(h) }
                    self.pairing[f.id] = result
                }
            }
        }
    }

    /// Four digits from the key, the same as the computer shows (PhoneFind.Code)
    static func code(_ line: String) -> String {
        let h = Array(SHA256.hash(data: Data(line.utf8)))
        let n = (UInt32(h[0]) << 24) | (UInt32(h[1]) << 16) | (UInt32(h[2]) << 8) | UInt32(h[3])
        return String(format: "%04u", n % 10000)
    }

    // MARK: UDP

    /// Sends the message to each address (a few times) and collects the JSON replies with their sender
    private static func exchange(_ message: [String: Any], to addresses: [String], tries: Int, wait: TimeInterval) -> [(String, [String: Any])] {
        guard let data = try? JSONSerialization.data(withJSONObject: message) else { return [] }
        let fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        guard fd >= 0 else { return [] }
        defer { close(fd) }
        var yes: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_BROADCAST, &yes, socklen_t(MemoryLayout<Int32>.size))
        var tv = timeval(tv_sec: 0, tv_usec: 200_000)
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))

        var out: [(String, [String: Any])] = []
        for _ in 0..<tries {
            for a in addresses {
                var to = sockaddr_in()
                to.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
                to.sin_family = sa_family_t(AF_INET)
                to.sin_port = port.bigEndian
                guard inet_pton(AF_INET, a, &to.sin_addr) == 1 else { continue }
                _ = data.withUnsafeBytes { b in
                    withUnsafePointer(to: &to) {
                        $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                            sendto(fd, b.baseAddress, b.count, 0, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
                        }
                    }
                }
            }
            let until = Date().addingTimeInterval(wait)
            var buf = [UInt8](repeating: 0, count: 4096)
            let size = buf.count
            while Date() < until {
                var from = sockaddr_in()
                var len = socklen_t(MemoryLayout<sockaddr_in>.size)
                let n = withUnsafeMutablePointer(to: &from) {
                    $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { recvfrom(fd, &buf, size, 0, $0, &len) }
                }
                guard n > 0 else { continue }
                var ipBuf = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
                inet_ntop(AF_INET, &from.sin_addr, &ipBuf, socklen_t(INET_ADDRSTRLEN))
                let ip = String(cString: ipBuf)
                guard let m = (try? JSONSerialization.jsonObject(with: Data(buf[0..<n]))) as? [String: Any],
                      m["litebro"] as? Int == 1 else { continue }
                out.append((ip, m))
            }
            if !out.isEmpty && tries > 1 { break }
        }
        return out
    }

    /// 255.255.255.255 and the broadcast address of each Wi-Fi or wired network of the phone
    private static func broadcasts() -> [String] {
        var list = ["255.255.255.255"]
        var ifs: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&ifs) == 0, let first = ifs else { return list }
        defer { freeifaddrs(ifs) }
        for p in sequence(first: first, next: { $0.pointee.ifa_next }) {
            let i = p.pointee
            guard let a = i.ifa_addr, a.pointee.sa_family == UInt8(AF_INET), let m = i.ifa_netmask,
                  (Int32(i.ifa_flags) & IFF_UP) != 0, (Int32(i.ifa_flags) & IFF_LOOPBACK) == 0 else { continue }
            let name = String(cString: i.ifa_name)
            guard name.hasPrefix("en") || name.hasPrefix("bridge") else { continue }   // not cellular (pdp_ip)
            let ip = a.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { $0.pointee.sin_addr.s_addr }
            let mask = m.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { $0.pointee.sin_addr.s_addr }
            var b = in_addr(s_addr: ip | ~mask)
            var buf = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
            inet_ntop(AF_INET, &b, &buf, socklen_t(INET_ADDRSTRLEN))
            let s = String(cString: buf)
            if !list.contains(s) { list.append(s) }
        }
        return list
    }
}
