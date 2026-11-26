import CryptoKit
import Foundation
import NIOCore
import NIOPosix
import NIOSSH

/// One SSH connection with a shell in a pseudo-terminal (SwiftNIO SSH). Callbacks come on the connection's
/// own thread; the terminal view takes output from any thread.
final class SSHConnection {
    enum Auth {
        case password(String)
        case key(Curve25519.Signing.PrivateKey)
    }

    enum Failure: LocalizedError {
        case hostKeyChanged(String)
        case signIn
        var errorDescription: String? {
            switch self {
            case .hostKeyChanged:
                return L("Ключ компьютера не совпадает с запомненным. Это другой компьютер или кто-то подменяет его в сети. Если на компьютере переустановили Windows или OpenSSH, отсканируйте его QR-код заново.")
            case .signIn:
                return L("Не удалось войти: неверный пароль или ключ не добавлен на компьютер.")
            }
        }
    }

    /// Output of the shell
    var onData: ((ArraySlice<UInt8>) -> Void)?
    /// Signed in and the shell is open
    var onOpen: (() -> Void)?
    /// The connection ended: nil when the shell exited, else why
    var onClose: ((String?) -> Void)?
    /// The computer's key at the first sign-in (no fingerprint was known)
    var onNewHostKey: ((String) -> Void)?

    private let group = MultiThreadedEventLoopGroup(numberOfThreads: 1)
    private var channel: Channel?
    private var shell: Channel?
    private var closed = false
    private var size = (cols: 80, rows: 24)

    deinit {
        channel?.close(promise: nil)
        group.shutdownGracefully { _ in }
    }

    func connect(to h: SSHHost, auth: [Auth], cols: Int, rows: Int) {
        size = (max(cols, 20), max(rows, 5))
        let offers: [NIOSSHUserAuthenticationOffer.Offer] = auth.map { a in
            switch a {
            case .password(let p): return .password(.init(password: p))
            case .key(let k): return .privateKey(.init(privateKey: NIOSSHPrivateKey(ed25519Key: k)))
            }
        }
        let user = h.user, expected = h.fingerprint
        let learned: (String) -> Void = { [weak self] fp in self?.onNewHostKey?(fp) }
        let events = ConnectionEvents(
            authed: { [weak self] in self?.openShell() },
            failed: { [weak self] e, signedIn in self?.finish(Self.describe(e, signedIn: signedIn)) })
        let bootstrap = ClientBootstrap(group: group)
            .connectTimeout(.seconds(8))
            .channelOption(ChannelOptions.socket(SocketOptionLevel(IPPROTO_TCP), TCP_NODELAY), value: 1)
            .channelInitializer { ch in
                ch.eventLoop.makeCompletedFuture {
                    let ssh = NIOSSHHandler(
                        role: .client(.init(userAuthDelegate: AuthOffers(user: user, offers: offers),
                                            serverAuthDelegate: HostKeyCheck(expected: expected, learned: learned))),
                        allocator: ch.allocator, inboundChildChannelInitializer: nil)
                    try ch.pipeline.syncOperations.addHandler(ssh)
                    try ch.pipeline.syncOperations.addHandler(events)
                }
            }
        bootstrap.connect(host: h.host, port: h.port).whenComplete { [weak self] r in
            switch r {
            case .success(let ch):
                self?.channel = ch
                ch.closeFuture.whenComplete { _ in self?.finish(events.signedIn ? nil : Failure.signIn.localizedDescription) }
            case .failure(let e):
                self?.finish(Self.describe(e))
            }
        }
    }

    private func openShell() {
        guard let channel else { return }
        let size = self.size
        let onData: (ArraySlice<UInt8>) -> Void = { [weak self] b in self?.onData?(b) }
        channel.pipeline.handler(type: NIOSSHHandler.self).whenSuccess { [weak self] ssh in
            let promise = channel.eventLoop.makePromise(of: Channel.self)
            ssh.createChannel(promise) { child, type in
                guard type == .session else { return child.eventLoop.makeFailedFuture(Failure.signIn) }
                return child.eventLoop.makeCompletedFuture {
                    try child.pipeline.syncOperations.addHandler(ShellHandler(cols: size.cols, rows: size.rows, onData: onData))
                }
            }
            promise.futureResult.whenComplete { r in
                switch r {
                case .success(let child):
                    self?.shell = child
                    self?.onOpen?()
                    // The shell exited: the connection has nothing more to do
                    child.closeFuture.whenComplete { _ in channel.close(promise: nil) }
                case .failure(let e):
                    self?.finish(Self.describe(e))
                }
            }
        }
    }

    func send(_ bytes: [UInt8]) {
        guard let shell else { return }
        shell.writeAndFlush(shell.allocator.buffer(bytes: bytes), promise: nil)
    }

    func resize(cols: Int, rows: Int) {
        size = (cols, rows)
        shell?.triggerUserOutboundEvent(
            SSHChannelRequestEvent.WindowChangeRequest(terminalCharacterWidth: cols, terminalRowHeight: rows,
                                                       terminalPixelWidth: 0, terminalPixelHeight: 0), promise: nil)
    }

    /// Runs a command on its own channel of the same connection and gives its output
    func exec(_ command: String, done: @escaping (String?) -> Void) {
        guard let channel else { return done(nil) }
        channel.pipeline.handler(type: NIOSSHHandler.self).whenComplete { r in
            guard case .success(let ssh) = r else { return done(nil) }
            let result = channel.eventLoop.makePromise(of: String.self)
            let promise = channel.eventLoop.makePromise(of: Channel.self)
            ssh.createChannel(promise) { child, _ in
                child.eventLoop.makeCompletedFuture {
                    try child.pipeline.syncOperations.addHandler(ExecHandler(command: command, result: result))
                }
            }
            promise.futureResult.whenFailure { result.fail($0) }
            result.futureResult.whenComplete { r in
                if case .success(let s) = r { done(s) } else { done(nil) }
            }
        }
    }

    func close() {
        closed = true
        channel?.close(promise: nil)
    }

    private func finish(_ why: String?) {
        if closed && why == nil { return }
        let report = closed ? nil : why
        closed = true
        onClose?(report)
        onClose = nil
    }

    private static func describe(_ e: Error, signedIn: Bool = true) -> String {
        if let f = e as? Failure { return f.localizedDescription }
        if e is NIOConnectionError || e is IOError || e is ChannelError {
            return L("Компьютер не отвечает. Проверьте, что телефон в той же сети Wi-Fi и вход по SSH на компьютере включён.")
        }
        // Refused offers end the connection with an error of the protocol
        return signedIn ? "\(e)" : Failure.signIn.localizedDescription
    }
}

/// Signed in, or the connection failed
private final class ConnectionEvents: ChannelInboundHandler {
    typealias InboundIn = Any
    private let authed: () -> Void
    private let failed: (Error, Bool) -> Void
    private(set) var signedIn = false

    init(authed: @escaping () -> Void, failed: @escaping (Error, Bool) -> Void) {
        self.authed = authed
        self.failed = failed
    }

    func userInboundEventTriggered(context: ChannelHandlerContext, event: Any) {
        if event is UserAuthSuccessEvent {
            signedIn = true
            authed()
        }
        context.fireUserInboundEventTriggered(event)
    }

    func errorCaught(context: ChannelHandlerContext, error: Error) {
        failed(error, signedIn)
        context.close(promise: nil)
    }
}

/// The ways to sign in, in order: the key, then the password
private final class AuthOffers: NIOSSHClientUserAuthenticationDelegate {
    private let user: String
    private var offers: [NIOSSHUserAuthenticationOffer.Offer]

    init(user: String, offers: [NIOSSHUserAuthenticationOffer.Offer]) {
        self.user = user
        self.offers = offers
    }

    func nextAuthenticationType(availableMethods: NIOSSHAvailableUserAuthenticationMethods,
                                nextChallengePromise: EventLoopPromise<NIOSSHUserAuthenticationOffer?>) {
        while !offers.isEmpty {
            let o = offers.removeFirst()
            var usable = false
            if case .privateKey = o { usable = availableMethods.contains(.publicKey) }
            if case .password = o { usable = availableMethods.contains(.password) }
            if usable {
                return nextChallengePromise.succeed(NIOSSHUserAuthenticationOffer(username: user, serviceName: "", offer: o))
            }
        }
        nextChallengePromise.succeed(nil)
    }
}

/// The computer's key: the one from its QR code, or remembered at the first sign-in
private final class HostKeyCheck: NIOSSHClientServerAuthenticationDelegate {
    private let expected: String?
    private let learned: (String) -> Void

    init(expected: String?, learned: @escaping (String) -> Void) {
        self.expected = expected
        self.learned = learned
    }

    func validateHostKey(hostKey: NIOSSHPublicKey, validationCompletePromise: EventLoopPromise<Void>) {
        guard let fp = SSHStore.fingerprint(ofLine: String(openSSHPublicKey: hostKey)) else {
            return validationCompletePromise.fail(SSHConnection.Failure.hostKeyChanged(""))
        }
        if let expected, expected != fp {
            return validationCompletePromise.fail(SSHConnection.Failure.hostKeyChanged(fp))
        }
        if expected == nil { learned(fp) }
        validationCompletePromise.succeed(())
    }
}

/// The shell in a pseudo-terminal of the computer
private final class ShellHandler: ChannelDuplexHandler {
    typealias InboundIn = SSHChannelData
    typealias InboundOut = ByteBuffer
    typealias OutboundIn = ByteBuffer
    typealias OutboundOut = SSHChannelData

    private let cols: Int, rows: Int
    private let onData: (ArraySlice<UInt8>) -> Void

    init(cols: Int, rows: Int, onData: @escaping (ArraySlice<UInt8>) -> Void) {
        self.cols = cols
        self.rows = rows
        self.onData = onData
    }

    func handlerAdded(context: ChannelHandlerContext) {
        _ = context.channel.setOption(ChannelOptions.allowRemoteHalfClosure, value: true)
    }

    func channelActive(context: ChannelHandlerContext) {
        let pty = SSHChannelRequestEvent.PseudoTerminalRequest(
            wantReply: false, term: "xterm-256color", terminalCharacterWidth: cols, terminalRowHeight: rows,
            terminalPixelWidth: 0, terminalPixelHeight: 0, terminalModes: SSHTerminalModes([:]))
        context.triggerUserOutboundEvent(pty, promise: nil)
        context.triggerUserOutboundEvent(SSHChannelRequestEvent.ShellRequest(wantReply: false), promise: nil)
        context.fireChannelActive()
    }

    func channelRead(context: ChannelHandlerContext, data: NIOAny) {
        let message = unwrapInboundIn(data)
        guard case .byteBuffer(let buffer) = message.data, let bytes = buffer.getBytes(at: buffer.readerIndex, length: buffer.readableBytes) else { return }
        onData(bytes[...])
    }

    func userInboundEventTriggered(context: ChannelHandlerContext, event: Any) {
        if let e = event as? ChannelEvent, e == .inputClosed { context.close(promise: nil) }
        context.fireUserInboundEventTriggered(event)
    }

    func write(context: ChannelHandlerContext, data: NIOAny, promise: EventLoopPromise<Void>?) {
        let buffer = unwrapOutboundIn(data)
        context.write(wrapOutboundOut(SSHChannelData(type: .channel, data: .byteBuffer(buffer))), promise: promise)
    }
}

/// One command, its output collected until it ends
private final class ExecHandler: ChannelDuplexHandler {
    typealias InboundIn = SSHChannelData
    typealias OutboundIn = Never
    typealias OutboundOut = SSHChannelData

    private let command: String
    private let result: EventLoopPromise<String>
    private var output: [UInt8] = []

    init(command: String, result: EventLoopPromise<String>) {
        self.command = command
        self.result = result
    }

    func handlerAdded(context: ChannelHandlerContext) {
        _ = context.channel.setOption(ChannelOptions.allowRemoteHalfClosure, value: true)
    }

    func channelActive(context: ChannelHandlerContext) {
        context.triggerUserOutboundEvent(SSHChannelRequestEvent.ExecRequest(command: command, wantReply: false), promise: nil)
        context.fireChannelActive()
    }

    func channelRead(context: ChannelHandlerContext, data: NIOAny) {
        let message = unwrapInboundIn(data)
        if case .byteBuffer(let buffer) = message.data, let b = buffer.getBytes(at: buffer.readerIndex, length: buffer.readableBytes) {
            output.append(contentsOf: b)
        }
    }

    func userInboundEventTriggered(context: ChannelHandlerContext, event: Any) {
        if let e = event as? ChannelEvent, e == .inputClosed { context.close(promise: nil) }
        context.fireUserInboundEventTriggered(event)
    }

    func channelInactive(context: ChannelHandlerContext) {
        result.succeed(String(decoding: output, as: UTF8.self))
        context.fireChannelInactive()
    }
}
