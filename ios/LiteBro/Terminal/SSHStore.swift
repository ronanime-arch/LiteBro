import CryptoKit
import Foundation

/// A computer for the terminal: LiteBro on Windows shows its QR code (start page, «С телефона» → «Терминал»)
struct SSHHost: Identifiable, Codable, Equatable, Hashable {
    var id = UUID()
    var name: String
    var host: String
    var port = 22
    var user: String
    /// The host key as ssh prints it (SHA256:…): from the QR code, or remembered at the first sign-in
    var fingerprint: String?
    /// Signs in with this iPhone's key (behind Face ID) instead of the password
    var useKey = false
}

final class SSHStore: ObservableObject {
    static let shared = SSHStore()

    @Published var hosts: [SSHHost] = [] { didSet { save() } }

    private static let defaultsKey = "sshHosts"
    private static let passService = "io.github.ronanime-arch.LiteBro.ssh"
    private static let keyService = "io.github.ronanime-arch.LiteBro.sshkey"
    private static let keyAccount = "ed25519"

    private init() {
        if let d = UserDefaults.standard.data(forKey: Self.defaultsKey),
           let list = try? JSONDecoder().decode([SSHHost].self, from: d) {
            hosts = list
        }
    }

    private func save() {
        if let d = try? JSONEncoder().encode(hosts) { UserDefaults.standard.set(d, forKey: Self.defaultsKey) }
    }

    func upsert(_ h: SSHHost) {
        if let i = hosts.firstIndex(where: { $0.id == h.id }) { hosts[i] = h } else { hosts.append(h) }
    }

    func remove(_ h: SSHHost) {
        hosts.removeAll { $0.id == h.id }
        Vault.shared.deleteSecret(service: Self.passService, account: h.id.uuidString)
    }

    /// From the QR code of LiteBro on Windows: litebro://ssh?host=…&port=…&user=…&name=…&fp=SHA256:…
    /// The code comes from the computer itself, so its key replaces a remembered one.
    func add(from url: URL) -> SSHHost? {
        guard url.scheme?.lowercased() == "litebro", url.host?.lowercased() == "ssh",
              let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems else { return nil }
        func value(_ k: String) -> String? { items.first { $0.name == k }?.value.flatMap { $0.isEmpty ? nil : $0 } }
        guard let host = value("host"), let user = value("user"),
              host.range(of: "^[A-Za-z0-9.:%-]+$", options: .regularExpression) != nil else { return nil }
        let port = value("port").flatMap(Int.init) ?? 22
        let fp = value("fp").flatMap { $0.hasPrefix("SHA256:") ? $0 : nil }
        if var h = hosts.first(where: { $0.host == host && $0.user == user && $0.port == port }) {
            if let fp { h.fingerprint = fp }
            if let n = value("name") { h.name = n }
            upsert(h)
            return h
        }
        let h = SSHHost(name: value("name") ?? host, host: host, port: port, user: user, fingerprint: fp)
        upsert(h)
        return h
    }

    // MARK: Password

    func hasPassword(_ h: SSHHost) -> Bool {
        Vault.shared.hasSecret(service: Self.passService, account: h.id.uuidString)
    }

    func savePassword(_ p: String, for h: SSHHost) {
        Vault.shared.setSecret(Data(p.utf8), service: Self.passService, account: h.id.uuidString)
        objectWillChange.send()
    }

    func deletePassword(_ h: SSHHost) {
        Vault.shared.deleteSecret(service: Self.passService, account: h.id.uuidString)
        objectWillChange.send()
    }

    func password(_ h: SSHHost, done: @escaping (String?) -> Void) {
        Vault.shared.secret(service: Self.passService, account: h.id.uuidString,
                            reason: L("Вход на компьютер") + " " + h.name) { d in
            done(d.map { String(decoding: $0, as: UTF8.self) })
        }
    }

    // MARK: This iPhone's key

    var hasKey: Bool { Vault.shared.hasSecret(service: Self.keyService, account: Self.keyAccount) }

    /// The key after Face ID; nil when cancelled
    func key(done: @escaping (Curve25519.Signing.PrivateKey?) -> Void) {
        Vault.shared.secret(service: Self.keyService, account: Self.keyAccount,
                            reason: L("Вход на компьютер по ключу")) { d in
            done(d.flatMap { try? Curve25519.Signing.PrivateKey(rawRepresentation: $0) })
        }
    }

    /// A new key, kept in the keychain of this device only
    func makeKey() -> Curve25519.Signing.PrivateKey {
        let k = Curve25519.Signing.PrivateKey()
        Vault.shared.setSecret(k.rawRepresentation, service: Self.keyService, account: Self.keyAccount)
        UserDefaults.standard.set(Self.publicLine(k), forKey: Self.publicKey)
        objectWillChange.send()
        return k
    }

    private static let publicKey = "sshPublicKey"

    /// This iPhone's key line for authorized_keys, on the main thread: made when there is none; an older key
    /// is read once behind Face ID. nil when cancelled.
    func publicKeyLine(done: @escaping (String?) -> Void) {
        if hasKey, let line = UserDefaults.standard.string(forKey: Self.publicKey) { return done(line) }
        guard hasKey else { return done(Self.publicLine(makeKey())) }
        key { k in
            let line = k.map { Self.publicLine($0) }
            if let line { UserDefaults.standard.set(line, forKey: Self.publicKey) }
            DispatchQueue.main.async { done(line) }
        }
    }

    /// As in authorized_keys: "ssh-ed25519 AAAA… litebro-iphone"
    static func publicLine(_ k: Curve25519.Signing.PrivateKey) -> String {
        var blob = Data()
        func put(_ d: Data) {
            var n = UInt32(d.count).bigEndian
            blob.append(Data(bytes: &n, count: 4))
            blob.append(d)
        }
        put(Data("ssh-ed25519".utf8))
        put(k.publicKey.rawRepresentation)
        return "ssh-ed25519 " + blob.base64EncodedString() + " litebro-iphone"
    }

    /// The fingerprint of a key line, as ssh and the QR code write it
    static func fingerprint(ofLine line: String) -> String? {
        let parts = line.split(separator: " ")
        guard parts.count >= 2, let blob = Data(base64Encoded: String(parts[1])) else { return nil }
        let b64 = Data(SHA256.hash(data: blob)).base64EncodedString()
        return "SHA256:" + b64.replacingOccurrences(of: "=", with: "")
    }
}
