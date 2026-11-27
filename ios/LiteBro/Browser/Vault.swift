import CommonCrypto
import CryptoKit
import Foundation
import LocalAuthentication
import Security
import WebKit

/// Passwords and bank cards in the keychain of the app: encrypted by iOS with the device key,
/// only on this device (no iCloud), and every read asks Face ID / Touch ID or the passcode
/// (an access control of the keychain item, enforced by the Secure Enclave, not by LiteBro).
/// Card security codes (CVC) are never kept.
final class Vault: ObservableObject {
    static let shared = Vault()

    struct Login: Identifiable, Hashable {
        let host: String
        let user: String
        /// A salted slow hash of the password ("p2:salt:hex", PBKDF2), to know a form sends the same one
        /// without asking Face ID; passwords saved before 2026-11-27 have "salt:sha256" until saved again
        let check: String
        var id: String { host + "\n" + user }
    }

    struct Card: Codable {
        var name: String
        var number: String
        var month: Int
        var year: Int
    }

    struct CardInfo: Identifiable, Hashable {
        let id: String
        let label: String
    }

    @Published private(set) var logins: [Login] = []
    @Published private(set) var cards: [CardInfo] = []

    private static let cardService = "io.github.ronanime-arch.LiteBro.card"

    init() { reload() }

    func reload() {
        logins = items(kSecClassInternetPassword).compactMap { a in
            guard let h = a[kSecAttrServer as String] as? String, let u = a[kSecAttrAccount as String] as? String else { return nil }
            return Login(host: h, user: u, check: a[kSecAttrComment as String] as? String ?? "")
        }.sorted { ($0.host, $0.user) < ($1.host, $1.user) }
        cards = items(kSecClassGenericPassword, service: Self.cardService).compactMap { a in
            guard let id = a[kSecAttrAccount as String] as? String else { return nil }
            return CardInfo(id: id, label: a[kSecAttrLabel as String] as? String ?? "••••")
        }
    }

    private func items(_ cls: CFString, service: String? = nil) -> [[String: Any]] {
        var q: [String: Any] = [kSecClass as String: cls, kSecReturnAttributes as String: true,
                                kSecMatchLimit as String: kSecMatchLimitAll]
        if let service { q[kSecAttrService as String] = service }
        var out: CFTypeRef?
        guard SecItemCopyMatching(q as CFDictionary, &out) == errSecSuccess else { return [] }
        return out as? [[String: Any]] ?? []
    }

    /// Biometrics or the passcode for every read; a device without a passcode cannot have that,
    /// then the item is kept without it (still encrypted, only while the device is unlocked)
    func protect(_ q: inout [String: Any]) {
        if LAContext().canEvaluatePolicy(.deviceOwnerAuthentication, error: nil),
           let acl = SecAccessControlCreateWithFlags(nil, kSecAttrAccessibleWhenUnlockedThisDeviceOnly, .userPresence, nil) {
            q[kSecAttrAccessControl as String] = acl
        } else {
            q[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
        }
    }

    // MARK: Passwords

    /// The keychain keeps this next to the password but outside its Face ID lock, so it must not be a fast
    /// hash: who reads the keychain's attributes would try passwords by the million against it
    static func checksum(_ pass: String, salt: String? = nil) -> String {
        let s = salt ?? UUID().uuidString
        let p = Array(pass.utf8CString.dropLast()), saltBytes = Array(s.utf8)
        var out = [UInt8](repeating: 0, count: 32)
        let status = CCKeyDerivationPBKDF(CCPBKDFAlgorithm(kCCPBKDF2), p, p.count, saltBytes, saltBytes.count,
                                          CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256), 150_000, &out, out.count)
        guard status == kCCSuccess else { return "" }
        return "p2:" + s + ":" + out.map { String(format: "%02x", $0) }.joined()
    }

    /// The same for a check written by an older build
    private static func oldChecksum(_ pass: String, salt: String) -> String {
        let d = SHA256.hash(data: Data((salt + pass).utf8))
        return salt + ":" + d.map { String(format: "%02x", $0) }.joined()
    }

    private static func isOld(_ check: String) -> Bool { !check.hasPrefix("p2:") }

    private static func matches(_ pass: String, _ check: String) -> Bool {
        let parts = check.split(separator: ":").map(String.init)
        if parts.count == 3, parts[0] == "p2" { return checksum(pass, salt: parts[1]) == check }
        if parts.count == 2 { return oldChecksum(pass, salt: parts[0]) == check }
        return false
    }

    /// Saved logins for a site: the same host, or one above or below it (accounts.x.com and x.com)
    func logins(for host: String) -> [Login] {
        let h = host.lowercased()
        return logins.filter { $0.host == h || h.hasSuffix("." + $0.host) || $0.host.hasSuffix("." + h) }
    }

    func isSaved(host: String, user: String, pass: String) -> Bool {
        guard let l = logins(for: host).first(where: { $0.user == user && Self.matches(pass, $0.check) }) else { return false }
        if Self.isOld(l.check) { upgradeCheck(l, pass: pass) }
        return true
    }

    /// An old fast check becomes the slow one, in place: only that attribute is written, the password is not
    /// touched, and nothing is asked (where iOS wants Face ID for it, the old check stays until the next save)
    private func upgradeCheck(_ l: Login, pass: String) {
        let quiet = LAContext()
        quiet.interactionNotAllowed = true
        let q: [String: Any] = [kSecClass as String: kSecClassInternetPassword, kSecAttrServer as String: l.host,
                                kSecAttrAccount as String: l.user, kSecUseAuthenticationContext as String: quiet]
        let check = Self.checksum(pass)
        guard !check.isEmpty else { return }
        if SecItemUpdate(q as CFDictionary, [kSecAttrComment as String: check] as CFDictionary) == errSecSuccess { reload() }
    }

    func save(host: String, user: String, pass: String) {
        let base: [String: Any] = [kSecClass as String: kSecClassInternetPassword,
                                   kSecAttrServer as String: host.lowercased(), kSecAttrAccount as String: user]
        SecItemDelete(base as CFDictionary)
        var add = base
        add[kSecValueData as String] = Data(pass.utf8)
        add[kSecAttrComment as String] = Self.checksum(pass)
        add[kSecAttrSynchronizable as String] = false
        protect(&add)
        let status = SecItemAdd(add as CFDictionary, nil)
        if status != errSecSuccess { NSLog("LiteBro: keychain add \(status)") }
        reload()
    }

    func delete(_ l: Login) {
        SecItemDelete([kSecClass as String: kSecClassInternetPassword, kSecAttrServer as String: l.host,
                       kSecAttrAccount as String: l.user] as CFDictionary)
        reload()
    }

    /// The password after Face ID; nil when the person cancelled
    func password(_ l: Login, reason: String, done: @escaping (String?) -> Void) {
        read([kSecClass as String: kSecClassInternetPassword, kSecAttrServer as String: l.host,
              kSecAttrAccount as String: l.user], reason: reason) { data in
            done(data.map { String(decoding: $0, as: UTF8.self) })
        }
    }

    // MARK: Cards

    static func luhn(_ number: String) -> Bool {
        let digits = number.compactMap(\.wholeNumberValue)
        guard (12...19).contains(digits.count) else { return false }
        let sum = digits.reversed().enumerated().reduce(0) { s, e in
            let d = e.offset % 2 == 1 ? e.element * 2 : e.element
            return s + (d > 9 ? d - 9 : d)
        }
        return sum % 10 == 0
    }

    static func brand(_ number: String) -> String {
        let n = number.filter(\.isNumber)
        if n.hasPrefix("4") { return "Visa" }
        if let two = Int(n.prefix(2)), (51...55).contains(two) { return "Mastercard" }
        if let four = Int(n.prefix(4)), (2221...2720).contains(four) { return "Mastercard" }
        if let four = Int(n.prefix(4)), (2200...2204).contains(four) { return L("Мир") }
        if n.hasPrefix("34") || n.hasPrefix("37") { return "American Express" }
        return L("Карта")
    }

    func addCard(_ c: Card) {
        guard let data = try? JSONEncoder().encode(c) else { return }
        let digits = c.number.filter(\.isNumber)
        var add: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                  kSecAttrService as String: Self.cardService,
                                  kSecAttrAccount as String: UUID().uuidString,
                                  kSecAttrLabel as String: Self.brand(digits) + " •••• " + String(digits.suffix(4)),
                                  kSecAttrSynchronizable as String: false,
                                  kSecValueData as String: data]
        protect(&add)
        let status = SecItemAdd(add as CFDictionary, nil)
        if status != errSecSuccess { NSLog("LiteBro: keychain add card \(status)") }
        reload()
    }

    func deleteCard(_ c: CardInfo) {
        SecItemDelete([kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: Self.cardService,
                       kSecAttrAccount as String: c.id] as CFDictionary)
        reload()
    }

    func card(_ c: CardInfo, reason: String, done: @escaping (Card?) -> Void) {
        read([kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: Self.cardService,
              kSecAttrAccount as String: c.id], reason: reason) { data in
            done(data.flatMap { try? JSONDecoder().decode(Card.self, from: $0) })
        }
    }

    // MARK: Other secrets (the terminal's passwords and key)

    func setSecret(_ data: Data, service: String, account: String) {
        let base: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
                                   kSecAttrService as String: service, kSecAttrAccount as String: account]
        SecItemDelete(base as CFDictionary)
        var add = base
        add[kSecValueData as String] = data
        add[kSecAttrSynchronizable as String] = false
        protect(&add)
        let status = SecItemAdd(add as CFDictionary, nil)
        if status != errSecSuccess { NSLog("LiteBro: keychain add \(status)") }
    }

    /// Whether it is there; asks nothing (the attributes are not protected, only the data)
    func hasSecret(service: String, account: String) -> Bool {
        items(kSecClassGenericPassword, service: service).contains { $0[kSecAttrAccount as String] as? String == account }
    }

    func secret(service: String, account: String, reason: String, done: @escaping (Data?) -> Void) {
        read([kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
              kSecAttrAccount as String: account], reason: reason, done: done)
    }

    func deleteSecret(service: String, account: String) {
        SecItemDelete([kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                       kSecAttrAccount as String: account] as CFDictionary)
    }

    // MARK: Reading

    /// Face ID comes from the keychain itself; the read waits for it off the main thread
    func read(_ query: [String: Any], reason: String, done: @escaping (Data?) -> Void) {
        let ctx = LAContext()
        ctx.localizedReason = reason
        var q = query
        q[kSecReturnData as String] = true
        q[kSecMatchLimit as String] = kSecMatchLimitOne
        q[kSecUseAuthenticationContext as String] = ctx
        DispatchQueue.global(qos: .userInitiated).async {
            var out: CFTypeRef?
            let status = SecItemCopyMatching(q as CFDictionary, &out)
            let data = status == errSecSuccess ? out as? Data : nil
            DispatchQueue.main.async { done(data) }
        }
    }
}

/// The page script for passwords and cards. It runs in a world of its own (`.defaultClient`):
/// pages cannot see it, call its fill functions or post to its handler.
enum VaultScript {
    static let source = #"""
(function () {
  if (window.__litebroVault) return;
  function post(m) { try { window.webkit.messageHandlers.litebroVault.postMessage(m); } catch (e) {} }
  function shown(e) { return e && e.offsetWidth > 0 && e.offsetHeight > 0 && !e.disabled; }
  function passwords() { return Array.prototype.filter.call(document.querySelectorAll('input[type=password]'), shown); }
  function userFor(p) {
    var all = Array.prototype.slice.call((p.form || document).querySelectorAll('input'));
    for (var i = all.indexOf(p) - 1; i >= 0; i--) {
      var t = (all[i].type || 'text').toLowerCase();
      if ((t === 'text' || t === 'email' || t === 'tel') && shown(all[i])) return all[i];
    }
    var auto = document.querySelector('input[autocomplete~=username], input[type=email]');
    return shown(auto) ? auto : null;
  }
  var cardSel = 'input[autocomplete~="cc-number"], input[name*="cardnumber" i], input[id*="cardnumber" i], ' +
                'input[name*="card_number" i], input[id*="card-number" i], input[name*="ccnumber" i]';
  // A sign-in that asks the name first and the password on the next page (PayPal, Google, Microsoft):
  // the one name field of a small form with a button
  var identSel = 'input[autocomplete~=username], input[type=email], input[name*=user i], input[id*=user i], ' +
                 'input[name*=email i], input[id*=email i], input[name*=login i], input[id*=login i]';
  function ident(form) {
    var forms = form ? [form] : Array.prototype.slice.call(document.forms);
    var found = [];
    forms.forEach(function (f) {
      var inputs = Array.prototype.filter.call(f.querySelectorAll('input:not([type=hidden])'), shown);
      if (inputs.length > 3 || !f.querySelector('button, input[type=submit], [role=button]')) return;
      Array.prototype.forEach.call(f.querySelectorAll(identSel), function (e) {
        var t = (e.type || 'text').toLowerCase();
        if ((t === 'text' || t === 'email' || t === 'tel') && shown(e)) found.push(e);
      });
    });
    return found.length === 1 ? found[0] : null;
  }
  // Fields of a sign-in form without the autocomplete hint get it, so the keyboard's own AutoFill (the
  // iPhone's passwords) knows the form; a hint the page set is left as it is
  function hint(e, v) { if (e && !e.hasAttribute('autocomplete')) e.setAttribute('autocomplete', v); }
  var told = {};
  function look() {
    var ps = passwords();
    if (ps.length === 1) {
      hint(ps[0], 'current-password');
      var u = userFor(ps[0]);
      if (u && ps[0].form && u.form === ps[0].form) hint(u, 'username');
    }
    else if (!ps.length) hint(ident(), 'username');
    if (!told.login && ps.length) { told.login = 1; post({ type: 'login' }); }
    if (!told.card && shown(document.querySelector(cardSel))) { told.card = 1; post({ type: 'card' }); }
  }
  var last = '';
  function grab(form) {
    var p = passwords().filter(function (e) { return e.value; })[0];
    if (!p) {
      // Only the form that is sent: a button or Enter in it
      var i = form && form.tagName === 'FORM' && !passwords().length && ident(form);
      if (i && i.value) post({ type: 'ident', user: i.value });
      return;
    }
    var u = userFor(p), key = (u ? u.value : '') + '\n' + p.value;
    if (key === last) return;
    last = key;
    post({ type: 'save', user: u ? u.value : '', pass: p.value });
  }
  document.addEventListener('submit', function (e) { grab(e.target); }, true);
  document.addEventListener('click', function (e) {
    var b = e.target && e.target.closest && e.target.closest('button, input[type=submit], [role=button]');
    if (b) setTimeout(function () { grab(b.form || b.closest('form')); }, 0);
  }, true);
  document.addEventListener('keydown', function (e) {
    var t = e.target && (e.target.type || '').toLowerCase();
    if (e.key === 'Enter' && (t === 'password' || t === 'text' || t === 'email' || t === 'tel')) grab(e.target.form);
  }, true);
  var timer = 0;
  new MutationObserver(function () { clearTimeout(timer); timer = setTimeout(look, 400); })
    .observe(document.documentElement, { childList: true, subtree: true });
  look();

  // Values set the way frameworks (React, Vue) notice them
  function put(el, v) {
    if (!el || v == null) return;
    var set = Object.getOwnPropertyDescriptor(Object.getPrototypeOf(el), 'value').set;
    el.focus();
    set.call(el, String(v));
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
  }
  function q(sel) { return Array.prototype.filter.call(document.querySelectorAll(sel), shown)[0]; }
  window.__litebroVault = {
    login: function (user, pass) {
      var p = passwords()[0];
      if (!p) return false;
      var u = userFor(p);
      if (u && user) put(u, user);
      put(p, pass);
      last = (user || '') + '\n' + pass;
      return true;
    },
    card: function (c) {
      var mm = ('0' + c.month).slice(-2), yy = String(c.year % 100), yyyy = String(c.year);
      put(q(cardSel), c.number);
      put(q('input[autocomplete~="cc-name"], input[name*="cardholder" i], input[name*="holder" i]'), c.name);
      var exp = q('input[autocomplete~="cc-exp"], input[name*="expir" i]:not([name*="month" i]):not([name*="year" i])');
      if (exp) put(exp, mm + '/' + yy);
      var m = q('input[autocomplete~="cc-exp-month"], select[autocomplete~="cc-exp-month"], [name*="month" i]');
      if (m) put(m, mm);
      var y = q('input[autocomplete~="cc-exp-year"], select[autocomplete~="cc-exp-year"], [name*="year" i]');
      if (y) put(y, (y.maxLength === 2 || (y.options && y.options.length && String(y.options[y.options.length - 1].value).length === 2)) ? yy : yyyy);
      var cvc = q('input[autocomplete~="cc-csc"], input[name*="cvc" i], input[name*="cvv" i]');
      if (cvc) cvc.focus();
      return true;
    }
  };
})();
"""#
}
