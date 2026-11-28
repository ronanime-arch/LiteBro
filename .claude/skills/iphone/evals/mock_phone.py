#!/usr/bin/env python3
"""A stand-in for the phone: the two servers LiteBro for iOS opens for a computer.

Mirrors ios/LiteBro/Phone/FileShare.swift (files, _touch, _install; port 8080) and
ios/Screen/SampleHandler.swift (pictures of the screen; port 8081) closely enough to test the helper
scripts and to rehearse a task without the phone. Keep it in step with those two files: every place
where it answers differently from the phone is a place where a helper bug can hide.

As a program it serves a small made-up phone until stopped:
    python mock_phone.py [--files-port N] [--screen-port N] [--no-control] [--read-only] [--old-build] [--log FILE]
"""
import argparse
import datetime
import email.utils
import io
import json
import os
import plistlib
import secrets
import shutil
import socket
import string
import sys
import tempfile
import threading
import time
import unicodedata
import urllib.parse
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


def token():
    """MiniHTTP.token(): 16 random bytes, base64 for addresses."""
    return secrets.token_urlsafe(16)


def nfd(s):
    """Names as iOS stores them: decomposed (й is и and a breve). APFS finds a name in either form."""
    return unicodedata.normalize("NFD", s)


def jpeg(lines, size=(591, 1280), background=(24, 24, 28), boxes=()):
    """A picture of a made-up screen: lines of text and labelled boxes (x, y, w, h, text, colour) in 0…1."""
    from PIL import Image, ImageDraw  # only the mock draws; the helper scripts need no library
    im = Image.new("RGB", size, background)
    d = ImageDraw.Draw(im)
    w, h = size
    for b in boxes:
        x, y, bw, bh, text = b[:5]
        colour = b[5] if len(b) > 5 else (60, 60, 70)
        d.rectangle([x * w, y * h, (x + bw) * w, (y + bh) * h], fill=colour, outline=(200, 200, 200))
        d.text((x * w + 8, y * h + 8), text, fill=(255, 255, 255))
    for i, line in enumerate(lines):
        d.text((16, 16 + i * 18), line, fill=(235, 235, 235))
    out = io.BytesIO()
    im.save(out, "JPEG", quality=70)
    return out.getvalue()


class FrameStore:
    """SampleHandler.FrameStore with processSampleBuffer in front of it.

    iOS hands a frame only when the screen changes. The build with GET frame (`keeps`) always holds the
    newest one and makes its picture when somebody asks. The older build made a picture only while somebody
    watched the stream and not more often than every 0.12 s, so its kept picture can be older than the
    screen and the last frame of a burst can be lost for good.
    """

    def __init__(self, idle=10.0, gap=0.12, keeps=True):
        self.cond = threading.Condition()
        self.idle = idle
        self.gap = gap
        self.keeps = keeps
        self.truth = None     # what the screen really shows
        self.on_join = None   # called when somebody starts watching, for the tests
        self.reset()

    def reset(self):
        """A new broadcast is a new run of the extension: no picture, number 0."""
        with self.cond:
            self.frame = None
            self.number = 0
            self.viewers = 0
            self.closed = False
            self.last = 0.0
            self.pending = self.keeps and self.truth is not None  # the frames of the broadcast's own start
            self.changed_at = time.monotonic()

    def _encode(self):
        self.frame = self.truth
        self.number += 1
        self.pending = False
        self.cond.notify_all()

    def screen_changed(self, picture):
        """ReplayKit handed a frame. True when its picture was made at once for the stream's viewers."""
        with self.cond:
            self.truth = picture
            self.changed_at = now = time.monotonic()
            if self.keeps:
                self.pending = True
            if self.viewers <= 0 or now - self.last < self.gap:
                return False
            self.last = now
            self._encode()
            return True

    def current(self):
        """The build that keeps: the screen as it is now, its number and for how long it has been still."""
        with self.cond:
            if self.pending:
                self._encode()
            if self.closed or self.frame is None:
                return None
            return self.frame, self.number, time.monotonic() - self.changed_at

    def join(self):
        with self.cond:
            self.viewers += 1
        if self.on_join:
            self.on_join()

    def leave(self):
        with self.cond:
            self.viewers -= 1

    def close(self):
        with self.cond:
            self.closed = True
            self.cond.notify_all()

    def next(self, after, wait=None):
        """The picture after the one numbered `after`; None once closed or after `wait` s without one."""
        deadline = time.monotonic() + (self.idle if wait is None else wait)
        with self.cond:
            while self.number <= after and not self.closed:
                left = deadline - time.monotonic()
                if left <= 0 or not self.cond.wait(left):
                    break
            if self.closed or self.number <= after or self.frame is None:
                return None
            return self.frame, self.number


class MockPhone:
    def __init__(self, root, files_port=0, screen_port=0, control=True, read_only=False, host="127.0.0.1",
                 idle=10.0, log=None, old_build=False, files_token=None, screen_token=None):
        self.root = os.path.abspath(root)
        self.rootreal = os.path.realpath(self.root)
        self.host = host
        self.files_port = files_port
        self.screen_port = screen_port
        self.files_token = files_token or token()
        self.screen_token = screen_token or token()
        self.control = control          # «Управление касаниями» with the screen permission still valid
        self.read_only = read_only      # «Только чтение»
        self.old_build = old_build      # the build before GET frame: see FrameStore, and a bad index kills it
        self.frames = FrameStore(idle=idle, keeps=not old_build)
        self.events = []                # what the computer asked for, in order
        self.lock = threading.Lock()
        self.on_event = None            # a made-up app reacting to taps: callable(event)
        self.installs = True            # TrollStore installs what _install asks for: the running LiteBro is gone
        self.magnifier = False          # TrollStore's URL scheme is off: the install command would open Magnifier
        self.dead_taps = False          # in-app fingers do nothing, as on the user's phone on 2026-11-27
        self.app_shot = True            # _remote/shot: LiteBro pictures its own screen (builds from 2026-11-28)
        self.leaves_after = 2.0         # s after Home or the side button until iOS freezes LiteBro; None: never
        self.log = log
        self._files = None
        self._frozen = None
        self._screen = None
        self._open = set()              # sockets of the requests being served

    # --- addresses ---

    @property
    def files_url(self):
        return "http://%s:%d/%s/" % (self.host, self.files_port, self.files_token)

    @property
    def screen_url(self):
        return "http://%s:%d/%s/" % (self.host, self.screen_port, self.screen_token)

    @property
    def bind(self):
        return "127.0.0.1" if self.host == "127.0.0.1" else "0.0.0.0"

    # --- life cycle ---

    def start(self, broadcast=True):
        self.resume()
        if broadcast:
            self.start_broadcast()
        return self

    def stop(self):
        self.close_access()
        self.stop_broadcast()

    def resume(self):
        """LiteBro on the screen with file access open: the files server answers (the same address)."""
        self._thaw()
        if not self._files:
            self._files = self._serve(FilesHandler, self.files_port)
            self.files_port = self._files.server_address[1]

    def suspend(self):
        """iOS froze LiteBro: it left the screen. The port stays open, connections are taken and never answered."""
        if not self._files:
            return
        self._stop_files()
        s = socket.socket()
        s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        for attempt in range(40):
            try:
                s.bind((self.bind, self.files_port))
                break
            except OSError:
                if attempt == 39:
                    raise
                time.sleep(0.05)  # the served connections are still letting go of the port
        s.listen(16)
        self._frozen = s

    def close_access(self):
        """«Закрыть доступ», the end of its time, or the app closed: nothing listens, connections are refused."""
        self._stop_files()
        self._thaw()

    def _thaw(self):
        s, self._frozen = self._frozen, None
        if s:
            s.close()

    def _stop_files(self):
        s, self._files = self._files, None
        if s:
            s.shutdown()
            s.server_close()
        for c in list(self._open):  # MiniHTTP.stop() closes the open connections too
            try:
                c.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass

    def start_broadcast(self):
        if self._screen:
            return
        self.frames.reset()
        self._screen = self._serve(ScreenHandler, self.screen_port)
        self.screen_port = self._screen.server_address[1]

    def stop_broadcast(self):
        s, self._screen = self._screen, None
        if s:
            self.frames.close()
            s.shutdown()
            s.server_close()

    def _serve(self, handler, port):
        server = ThreadingHTTPServer((self.bind, port), handler)
        server.daemon_threads = True
        server.phone = self
        threading.Thread(target=server.serve_forever, kwargs={"poll_interval": 0.05}, daemon=True).start()
        return server

    # --- what happened ---

    def note(self, e):
        if self.log:
            with open(self.log, "a", encoding="utf-8") as f:
                f.write(json.dumps(e, ensure_ascii=False) + "\n")

    def event(self, e):
        e["at"] = round(time.time(), 3)
        with self.lock:
            self.events.append(e)
        self.note(e)
        if self.on_event:
            try:
                self.on_event(e)
            except Exception as error:  # the made-up app must not take the server down
                sys.stderr.write("mock app: %r\n" % (error,))
        if e["kind"] == "button" and e.get("via") == "app" and self.leaves_after is not None:
            timer = threading.Timer(self.leaves_after, self.suspend)  # it still answers for a moment
            timer.daemon = True
            timer.start()
        if e["kind"] == "install" and self.installs:
            timer = threading.Timer(0.5, self.close_access)  # TrollStore puts the build in LiteBro's place
            timer.daemon = True
            timer.start()

    def take_events(self):
        with self.lock:
            out, self.events = self.events, []
        return out

    # --- files ---

    def stored(self, folder, name):
        """The name as the folder holds it: APFS finds a name in either Unicode form."""
        try:
            names = os.listdir(folder)
        except OSError:
            return name
        want = nfd(name)
        return next((n for n in names if n == name), next((n for n in names if nfd(n) == want), name))

    def resolve(self, uri):
        """FileShare.file(_:): the file of a request path, only inside the shared folder."""
        path = uri.split("?", 1)[0]
        parts = [urllib.parse.unquote(p) for p in path.split("/") if p]
        if not parts or parts[0] != self.files_token:
            return None
        parts = parts[1:]
        if any(p in ("..", ".") or "\0" in p for p in parts):
            return None
        full = self.root
        for p in parts:  # never os.path.join: a part that begins with a slash (from %2F) must stay inside
            for piece in [x for x in p.replace("\\", "/").split("/") if x not in ("", ".", "..")]:
                full = full + os.sep + self.stored(full, piece)
        # Through a link nothing outside the folder; for a new file its folder is checked, the nearest that is there
        there = full
        while not os.path.exists(there) and os.path.dirname(there) != there:
            there = os.path.dirname(there)
        real = os.path.realpath(there)
        if not (real == self.rootreal or real.startswith(self.rootreal + os.sep)):
            return None
        return full, parts

    def through_link(self, parts):
        """FileShare.throughLink: the path goes through a link before its last part. Nothing is changed
        through a link: a program that empties a linked folder file by file would empty what it leads to."""
        full = self.root
        for p in parts[:-1]:
            for piece in [x for x in p.replace("\\", "/").split("/") if x not in ("", ".", "..")]:
                full = full + os.sep + self.stored(full, piece)
                if os.path.islink(full):
                    return True
        return False

    def own_picture(self):
        """Remote.shot: LiteBro's own windows as a JPEG (the points of the screen twice over); None: no window."""
        app = self.on_event.__self__ if self.on_event else None
        if app is None:
            return jpeg(["LiteBro"], size=(780, 1688))
        return app.own_picture()

    def href(self, rel, is_dir):
        enc = [urllib.parse.quote(p, safe="-._~:") for p in rel]
        return "/" + "/".join([self.files_token] + enc) + ("/" if (is_dir or not rel) else "")


def xml_escape(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace('"', "&quot;")


REASONS = {100: "Continue", 200: "OK", 201: "Created", 204: "No Content", 206: "Partial Content",
           207: "Multi-Status", 301: "Moved Permanently", 400: "Bad Request", 403: "Forbidden", 404: "Not Found",
           405: "Method Not Allowed", 409: "Conflict", 412: "Precondition Failed", 416: "Range Not Satisfiable",
           431: "Request Header Fields Too Large"}
CSP = ("default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src 'self'; "
       "media-src 'self'; connect-src 'self'")


class Handler(BaseHTTPRequestHandler):
    """What MiniHTTP does for both servers: only the headers the handler lists, plus Content-Length."""
    protocol_version = "HTTP/1.1"
    announces_close = True  # the files server says `Connection: close` when it closes after the answer

    def log_message(self, *a):
        pass

    def setup(self):
        BaseHTTPRequestHandler.setup(self)
        self.server.phone._open.add(self.connection)

    def finish(self):
        self.server.phone._open.discard(self.connection)
        try:
            BaseHTTPRequestHandler.finish(self)
        except OSError:
            pass

    def __getattr__(self, name):
        # Every method comes to one place, as on the phone (it upper-cases the method)
        if name.startswith("do_"):
            return self.handle_any
        raise AttributeError(name)

    def send_response(self, code, message=None):
        self.send_response_only(code, message)  # no Date and no Server: the phone sends neither

    def send_error(self, code, message=None, explain=None):
        # A request the phone cannot read: 400 with nothing in it, and the connection ends
        self.close_connection = True
        self.respond(431 if code == 431 else 400)

    def respond(self, status, headers=(), body=None):
        self.send_response(status, REASONS.get(status, "Status"))
        for n, v in headers:
            self.send_header(n, v)
        self.send_header("Content-Length", str(len(body) if body else 0))
        if self.close_connection and self.announces_close:
            self.send_header("Connection", "close")
        self.end_headers()
        if body:
            self.wfile.write(body)

    def read_body(self, sink=None, limit=1 << 20):
        """The body as MiniHTTP reads it: chunked or by Content-Length. None when the connection ended early."""
        body = b""

        def take(piece):
            nonlocal body
            if sink:
                sink.write(piece)
            elif len(body) < limit:
                body += piece

        if "chunked" in (self.headers.get("Transfer-Encoding") or "").lower():
            while True:
                head = self.rfile.readline()
                if not head:
                    return None
                try:
                    size = int(head.split(b";")[0].strip() or b"0", 16)
                except ValueError:
                    return None
                if not size:
                    while self.rfile.readline().strip():
                        pass  # trailers
                    return body
                piece = self.rfile.read(size)
                if len(piece) < size:
                    return None
                take(piece)
                self.rfile.readline()
        text = (self.headers.get("Content-Length") or "").strip()
        left = int(text) if text.isdigit() else 0
        while left > 0:
            piece = self.rfile.read(min(left, 256 * 1024))
            if not piece:
                return None
            left -= len(piece)
            take(piece)
        return body

    @staticmethod
    def json_body(body):
        """JSONSerialization: UTF-8, UTF-16 or UTF-32 with or without a mark; an object, or nothing."""
        if body[:3] == b"\xef\xbb\xbf":
            encoding = "utf-8-sig"
        elif body[:4] in (b"\xff\xfe\x00\x00", b"\x00\x00\xfe\xff"):
            encoding = "utf-32"
        elif body[:2] in (b"\xff\xfe", b"\xfe\xff"):
            encoding = "utf-16"
        elif len(body) >= 4 and body[1:4] == b"\x00\x00\x00":
            encoding = "utf-32-le"
        elif len(body) >= 4 and body[:3] == b"\x00\x00\x00":
            encoding = "utf-32-be"
        elif len(body) >= 2 and body[1:2] == b"\x00":
            encoding = "utf-16-le"
        elif len(body) >= 2 and body[:1] == b"\x00":
            encoding = "utf-16-be"
        else:
            encoding = "utf-8"

        def refuse(name):
            raise ValueError(name)

        try:
            m = json.loads(body.decode(encoding), parse_constant=refuse)
            json.dumps(m, ensure_ascii=False).encode("utf-8")  # half of a surrogate pair is an error there
        except (ValueError, UnicodeError):
            return None
        return m if isinstance(m, dict) else None

    def decoded_path(self):
        # The phone reads the request line as UTF-8; http.server took it for Latin-1
        try:
            return self.path.encode("iso-8859-1").decode("utf-8", "replace")
        except UnicodeError:
            return self.path


def number(v):
    return isinstance(v, (int, float))


def flag(v):
    """A Swift Bool from JSON: true, false, 0 or 1."""
    return isinstance(v, bool) or (isinstance(v, (int, float)) and v in (0, 1))


def whole(v):
    """A Swift Int from JSON: a whole number."""
    return not isinstance(v, bool) and isinstance(v, (int, float)) and float(v).is_integer()


def text(v):
    return isinstance(v, str)


OPTIONS = {"recipe": text, "client": text, "sender": text, "masks": text, "bks": flag, "points": flag, "parentXY": flag,
           "index": whole, "identity": whole, "builtIn": flag, "pressure": number, "twist": number, "radius": number,
           "inApp": flag}
CRASH = "crash"


def touch_event(m, via, old_build=False):
    """What a touch request means to Touch.swift: an event, None for JSON it ignores (still answered 204),
    or CRASH where the older build trapped (an index or identity that is no UInt32)."""
    if isinstance(m.get("button"), str):
        return {"kind": "button", "button": "side" if m["button"] == "side" else "home", "via": via}
    if isinstance(m.get("type"), str) and via == "app":
        return {"kind": "type", "text": m["type"], "via": via}
    if m.get("t") in ("down", "move", "up") and number(m.get("x")) and number(m.get("y")):
        opts = {}
        for k, fits in OPTIONS.items():
            if k in m and fits(m[k]):
                v = m[k]
                if fits is flag:
                    v = bool(v)
                elif fits is whole:
                    v = int(v)
                    if not 0 <= v <= 0xFFFFFFFF:
                        if old_build:
                            return CRASH
                        continue  # UInt32(exactly:) gives nothing: the option keeps its default
                opts[k] = v
        return {"kind": "finger", "t": m["t"], "x": min(max(float(m["x"]), 0.0), 1.0),
                "y": min(max(float(m["y"]), 0.0), 1.0), "inApp": opts.get("inApp") is True, "opts": opts, "via": via}
    return None


URL_OK = set(string.ascii_letters + string.digits + "-._~:/?#[]@!$&'()*+,;=%")
# FileShare.ownBuild: the one build a command from the computer may install
OWN_BUILD = "https://github.com/ronanime-arch/LiteBro/releases/download/ios/LiteBro-PC-install.tipa"


class FilesHandler(Handler):
    def handle_any(self):
        ph = self.server.phone
        method, uri = self.command.upper(), self.decoded_path()
        path = uri.split("?", 1)[0]
        f = ph.resolve(uri)
        upload = None
        t = ph.files_token
        command = path == "/%s/_touch" % t or path.startswith("/%s/_remote/" % t)  # not files: nothing is kept of a PUT
        if method == "PUT" and not ph.read_only and f and not command and not ph.through_link(f[1]):
            temp = os.path.join(os.path.dirname(f[0]), ".litebro-upload-" + uuid.uuid4().hex)
            try:
                upload = (open(temp, "wb"), temp, f[0])
            except OSError:
                upload = None
        body = self.read_body(upload[0] if upload else None)
        if upload:
            upload[0].close()

        def drop_upload():
            if upload and os.path.exists(upload[1]):
                os.remove(upload[1])

        if body is None:
            # The connection ended in the middle of the body: no answer, the target as it was
            drop_upload()
            self.close_connection = True
            return
        t = ph.files_token

        if path == "/%s/_install" % t and method == "POST":
            drop_upload()
            m = self.json_body(body)
            url = m.get("url") if m else None
            # Only LiteBro's own released build, by its exact address. Builds before that rule took any http(s)
            # address URL(string:) of iOS 16 could read: no space, no letter outside ASCII, the scheme as written.
            good = url == OWN_BUILD
            if ph.old_build:
                good = isinstance(url, str) and set(url) <= URL_OK and url.split(":", 1)[0] in ("http", "https") and "://" in url
            if not (ph.control and good):
                return self.respond(403)
            if ph.magnifier:
                return self.respond(409, [("Content-Type", "text/plain; charset=utf-8")],
                                    "Команду установки примет не TrollStore: включите в настройках TrollStore схему URL (URL Scheme).".encode("utf-8"))
            self.respond(202)
            return ph.event({"kind": "install", "url": url})
        if path.startswith("/%s/_remote/" % t) and not ph.old_build:
            drop_upload()
            if not ph.control:
                return self.respond(403)
            if path == "/%s/_remote/shot" % t and ph.app_shot:
                picture = ph.own_picture()
                if picture is None:
                    return self.respond(409)
                return self.respond(200, [("Content-Type", "image/jpeg"), ("Cache-Control", "no-store")], picture)
            status, answer = self.remote(path[len("/%s/_remote/" % t):], self.json_body(body) or {})
            return self.respond(status, [("Content-Type", "application/json; charset=utf-8")],
                                json.dumps(answer, ensure_ascii=False).encode("utf-8"))
        if path == "/%s/_touch" % t:
            drop_upload()
            if not ph.control:
                return self.respond(403)
            m = self.json_body(body) if method == "POST" else None
            if m is not None:
                e = touch_event(m, "app", ph.old_build)
                if e == CRASH:
                    # LiteBro is gone: no answer, and nothing listens until the person opens it and access again
                    self.close_connection = True
                    ph.note({"kind": "crash", "why": "index or identity out of range", "via": "app"})
                    threading.Thread(target=ph.close_access, daemon=True).start()
                    return
                if e and e["kind"] == "finger" and e["inApp"] and not ph.old_build:
                    # An in-app finger is answered with what happened to it
                    app = ph.on_event.__self__ if ph.on_event else None
                    info = {"window": True, "key": True, "context": 1234, "enqueue": True,
                            "x": round(e["x"] * 390, 2), "y": round(e["y"] * 844, 2),
                            "hit": app.hit(e["x"], e["y"]) if app else "UIView"}
                    self.respond(200, [("Content-Type", "application/json")], json.dumps(info).encode("utf-8"))
                    return ph.event(e)
                self.respond(204)
                return ph.event(e) if e else None
            # Anything else, a HEAD too, gets the report with its body
            return self.respond(200, [("Content-Type", "application/json")],
                                b'{"available":true,"simple":true,"withType":true,"bks":true,"recipes":["trollvnc"]}')
        if f is None:
            drop_upload()
            return self.respond(404)
        full, rel = f
        if method in ("PUT", "DELETE", "MKCOL", "MOVE", "COPY", "PROPPATCH") and (ph.read_only or ph.through_link(rel)):
            drop_upload()
            return self.respond(403)
        exists = os.path.exists(full)  # follows a link, as fileExists does
        is_dir = os.path.isdir(full)

        if method == "OPTIONS":
            return self.respond(200, [("DAV", "1, 2"), ("MS-Author-Via", "DAV"),
                                      ("Allow", "OPTIONS, GET, HEAD, PUT, DELETE, MKCOL, MOVE, COPY, PROPFIND, PROPPATCH, LOCK, UNLOCK")])
        if method == "PROPFIND":
            if not exists:
                return self.respond(404)
            xml = '<?xml version="1.0" encoding="utf-8"?>\n<D:multistatus xmlns:D="DAV:">\n' + self.entry(full, rel)
            if is_dir and (self.headers.get("Depth") or "1") != "0":
                try:
                    names = os.listdir(full)
                except OSError:
                    names = []
                for n in names:
                    if not n.startswith(".litebro-upload-"):
                        xml += self.entry(os.path.join(full, n), rel + [nfd(n)])  # names come as iOS stores them
            xml += "</D:multistatus>\n"
            return self.respond(207, [("Content-Type", "application/xml; charset=utf-8")], xml.encode("utf-8"))
        if method == "PROPPATCH":
            xml = ('<?xml version="1.0" encoding="utf-8"?>\n<D:multistatus xmlns:D="DAV:"><D:response><D:href>'
                   + xml_escape(ph.href(rel, is_dir)) + "</D:href><D:propstat><D:prop/><D:status>HTTP/1.1 200 OK</D:status>"
                   "</D:propstat></D:response></D:multistatus>")
            return self.respond(207, [("Content-Type", "application/xml; charset=utf-8")], xml.encode("utf-8"))
        if method in ("GET", "HEAD"):
            if not exists:
                return self.respond(404)
            if is_dir:
                if not path.endswith("/"):
                    return self.respond(301, [("Location", ph.href(rel, True))])
                page = self.listing(full, rel).encode("utf-8")
                # A HEAD of a folder says Content-Length: 0 on the phone
                return self.respond(200, [("Content-Type", "text/html; charset=utf-8"), ("Cache-Control", "no-store"),
                                          ("Content-Security-Policy", CSP)], page if method == "GET" else None)
            return self.send_file(full, method)
        if method == "PUT":
            if not upload:
                return self.respond(409)
            if is_dir:
                # Never a file in place of a folder (before this build replaceItemAt may have taken the folder)
                drop_upload()
                return self.respond(405)
            had = os.path.exists(upload[2])
            try:
                os.replace(upload[1], upload[2])
            except OSError:
                drop_upload()
                return self.respond(403)
            return self.respond(204 if had else 201)
        if method == "DELETE":
            if not exists or not rel:
                return self.respond(403 if exists else 404)
            try:
                shutil.rmtree(full) if is_dir and not os.path.islink(full) else os.remove(full)
            except OSError:
                return self.respond(403)
            return self.respond(204)
        if method == "MKCOL":
            if exists:
                return self.respond(405)
            try:
                os.mkdir(full)
            except OSError:
                return self.respond(409)
            return self.respond(201)
        if method in ("MOVE", "COPY"):
            d = self.headers.get("Destination")
            if d:
                try:
                    d = d.encode("iso-8859-1").decode("utf-8", "replace")
                except UnicodeError:
                    pass
            to = ph.resolve(urllib.parse.urlsplit(d).path if "://" in d else d) if d else None
            if not exists or not rel or not to:
                return self.respond(400)
            if ph.through_link(to[1]):
                return self.respond(403)
            overwrite = (self.headers.get("Overwrite") or "").upper() != "F"
            had = os.path.lexists(to[0])
            if had and not overwrite:
                return self.respond(412)
            try:
                if had:
                    shutil.rmtree(to[0]) if os.path.isdir(to[0]) and not os.path.islink(to[0]) else os.remove(to[0])
                if method == "MOVE":
                    os.rename(full, to[0])
                elif is_dir:
                    shutil.copytree(full, to[0])
                else:
                    shutil.copy2(full, to[0])
            except OSError:
                return self.respond(409)
            return self.respond(204 if had else 201)
        if method == "LOCK":
            lock = "opaquelocktoken:" + str(uuid.uuid4())
            xml = ('<?xml version="1.0" encoding="utf-8"?>\n<D:prop xmlns:D="DAV:"><D:lockdiscovery><D:activelock>'
                   "<D:locktype><D:write/></D:locktype><D:lockscope><D:exclusive/></D:lockscope><D:depth>0</D:depth>"
                   "<D:timeout>Second-3600</D:timeout><D:locktoken><D:href>" + lock + "</D:href></D:locktoken>"
                   "</D:activelock></D:lockdiscovery></D:prop>")
            return self.respond(200, [("Content-Type", "application/xml; charset=utf-8"), ("Lock-Token", "<%s>" % lock)],
                                xml.encode("utf-8"))
        if method == "UNLOCK":
            return self.respond(204)
        return self.respond(405)

    def remote(self, name, m):
        """Remote.swift: state, open, js, click on the made-up LiteBro."""
        ph = self.server.phone
        app = ph.on_event.__self__ if ph.on_event else None

        def state():
            if not app:
                return {"error": "no browser"}
            page = app.screen == "page"
            return {"url": app.url if page else "", "title": app.title() if page else "", "loading": False,
                    "tabs": 1, "page": page}

        if name == "state":
            return 200, state()
        if name == "open":
            url = m.get("url")
            if not (isinstance(url, str) and set(url) <= URL_OK and ":" in url and app):
                return 400, {"error": "url"}
            ph.event({"kind": "open", "url": url, "newTab": m.get("newTab") is True, "via": "app"})
            return 200, state()
        if name == "js":
            code = m.get("code")
            if not isinstance(code, str):
                return 400, {"error": "code"}
            if not app or app.screen != "page":
                return 409, {"error": "no page"}
            ph.event({"kind": "js", "code": code, "via": "app"})
            return 200, app.script(code)
        if name == "click":
            if not (number(m.get("x")) and number(m.get("y"))):
                return 400, {"error": "x, y"}
            x, y = min(max(float(m["x"]), 0.0), 1.0), min(max(float(m["y"]), 0.0), 1.0)
            hit = app.hit(x, y) if app else "UIView"
            control = hit if hit in ("UIButton", "UITextField") else None
            ph.event({"kind": "click", "x": x, "y": y, "control": control, "via": "app"})
            return 200, {"hit": hit, "control": control}
        return 404, {"error": "unknown command"}

    def entry(self, full, rel):
        ph = self.server.phone
        try:
            st = os.stat(full)  # what a link leads to
        except OSError:
            st = None
        is_dir = os.path.isdir(full)
        mod = st.st_mtime if st else time.time()
        x = "<D:response><D:href>" + xml_escape(ph.href(rel, is_dir)) + "</D:href><D:propstat><D:prop>"
        x += "<D:displayname>" + xml_escape(rel[-1] if rel else "") + "</D:displayname>"
        if is_dir:
            x += "<D:resourcetype><D:collection/></D:resourcetype>"
        else:
            x += "<D:resourcetype/><D:getcontentlength>%d</D:getcontentlength><D:getcontenttype>%s</D:getcontenttype>" % (
                st.st_size if st else 0, mime(full))
        x += "<D:getlastmodified>" + email.utils.formatdate(mod, usegmt=True) + "</D:getlastmodified>"
        x += "<D:creationdate>" + datetime.datetime.fromtimestamp(mod, datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ") + "</D:creationdate>"
        x += ("<D:supportedlock><D:lockentry><D:lockscope><D:exclusive/></D:lockscope><D:locktype><D:write/></D:locktype>"
              "</D:lockentry></D:supportedlock></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>\n")
        return x

    def send_file(self, full, method):
        """FileShare.send: a file in pieces, with Range."""
        try:
            handle = open(full, "rb")
        except OSError:
            return self.respond(403)
        with handle:
            st = os.fstat(handle.fileno())
            size = st.st_size
            start, end, partial = 0, size - 1, False
            r = self.headers.get("Range")
            if r and r.startswith("bytes=") and "," not in r:
                p = r[6:].split("-")
                if len(p) == 2:
                    if p[0] == "" and p[1].isdigit():
                        start = max(0, size - int(p[1]))
                    elif p[0].isdigit():
                        start = int(p[0])
                        if p[1].isdigit():
                            end = min(int(p[1]), size - 1)
                    partial = True
            if size > 0 and (start > end or start >= size):
                return self.respond(416, [("Content-Range", "bytes */%d" % size)])
            length = 0 if size == 0 else end - start + 1
            self.send_response(206 if partial else 200)
            kind = mime(full)
            self.send_header("Content-Type", kind)
            self.send_header("Content-Length", str(length))
            self.send_header("Accept-Ranges", "bytes")
            self.send_header("X-Content-Type-Options", "nosniff")
            if any(x in kind for x in ("html", "xml", "svg")):
                self.send_header("Content-Security-Policy", "sandbox")  # a file of the phone is shown, never run
            self.send_header("Last-Modified", email.utils.formatdate(st.st_mtime, usegmt=True))
            if partial:
                self.send_header("Content-Range", "bytes %d-%d/%d" % (start, end, size))
            if self.close_connection:
                self.send_header("Connection", "close")
            self.end_headers()
            if method == "GET" and length > 0:
                handle.seek(start)
                left = length
                while left > 0:
                    piece = handle.read(min(left, 256 * 1024))
                    if not piece:
                        break
                    left -= len(piece)
                    self.wfile.write(piece)

    def listing(self, full, rel):
        ph = self.server.phone
        rows = '<tr><td><a href="../">..</a></td></tr>' if rel else ""
        for n in sorted(os.listdir(full)):
            if n.startswith(".litebro-upload-"):
                continue
            is_dir = os.path.isdir(os.path.join(full, n))
            rows += '<tr><td><a href="%s">%s%s</a></td></tr>' % (ph.href(rel + [nfd(n)], is_dir), "📁 " if is_dir else "", xml_escape(nfd(n)))
        return "<!doctype html><html><head><meta charset=utf-8><title>Телефон</title></head><body><table>%s</table></body></html>" % rows


MIME = {"jpg": "image/jpeg", "jpeg": "image/jpeg", "png": "image/png", "heic": "image/heic", "mov": "video/quicktime",
        "mp4": "video/mp4", "txt": "text/plain", "log": "text/plain", "json": "application/json", "pdf": "application/pdf",
        "plist": "application/x-plist", "html": "text/html"}


def mime(path):
    """One table for every computer the mock runs on (the phone asks UTType)."""
    return MIME.get(os.path.splitext(path)[1][1:].lower(), "application/octet-stream")


class ScreenHandler(Handler):
    announces_close = False

    def handle_any(self):
        ph = self.server.phone
        self.close_connection = False  # this server keeps every connection but the stream's, whatever was asked
        path = self.decoded_path().split("?", 1)[0]
        body = self.read_body(limit=4096)
        if body is None:
            self.close_connection = True
            return
        t = ph.screen_token
        if path in ("/%s/" % t, "/%s" % t):
            page = ('<!doctype html><html><head><meta charset=utf-8><title>Экран телефона</title></head><body>'
                    '<img src="stream" alt="">%s</body></html>' % ("<div id=bar></div>" if ph.control else ""))
            # Whatever the method, a HEAD too: the page with its body
            return self.respond(200, [("Content-Type", "text/html; charset=utf-8"), ("Cache-Control", "no-store")],
                                page.encode("utf-8"))
        if path == "/%s/frame" % t and ph.frames.keeps:
            return self.frame()
        if path == "/%s/stream" % t:
            return self.stream()
        if path == "/%s/touch" % t and self.command.upper() == "POST":
            m = self.json_body(body)
            if not ph.control or m is None:
                return self.respond(403)
            e = touch_event(m, "screen", ph.old_build)  # the extension's sandbox keeps it from the HID system: no effect
            if e == CRASH:
                self.close_connection = True
                ph.note({"kind": "crash", "why": "index or identity out of range", "via": "screen"})
                threading.Thread(target=ph.stop_broadcast, daemon=True).start()
                return
            self.respond(204)
            return ph.event(e) if e else None
        return self.respond(404)

    def frame(self):
        """GET frame: the screen as it is now; with ?after=N nothing (204) unless there is a newer picture."""
        now = self.server.phone.frames.current()
        if not now:
            return self.respond(503)
        picture, n, still = now
        state = [("X-Frame", str(n)), ("X-Frame-Age", str(int(still * 1000))), ("Cache-Control", "no-store")]
        query = urllib.parse.parse_qs(urllib.parse.urlsplit(self.path).query)
        after = (query.get("after") or [""])[0]
        if after.lstrip("-").isdigit() and n <= int(after):
            return self.respond(204, state)
        self.respond(200, state + [("Content-Type", "image/jpeg")], picture)

    def stream(self):
        """multipart/x-mixed-replace. The build that keeps: the screen as it is now, then every change, and the
        same picture again every 5 s of a still screen. The older build: the kept picture at once, then each
        new one, and the end after `idle` s of nothing."""
        frames = self.server.phone.frames
        boundary = "litebroframe"
        self.send_response(200)
        self.send_header("Content-Type", "multipart/x-mixed-replace; boundary=" + boundary)
        self.send_header("Cache-Control", "no-store")
        self.send_header("Connection", "close")
        self.end_headers()
        self.close_connection = True

        def send(picture):
            part = ("--%s\r\nContent-Type: image/jpeg\r\nContent-Length: %d\r\n\r\n" % (boundary, len(picture))).encode()
            self.wfile.write(part + picture + b"\r\n")
            self.wfile.flush()

        frames.join()
        try:
            seen = 0
            if not frames.keeps:
                while True:
                    got = frames.next(seen)
                    if not got:
                        break
                    seen = got[1]
                    send(got[0])
                return
            frames.current()
            sent = time.monotonic()
            while not frames.closed:
                got = frames.next(seen, 0.5)
                if got:
                    seen = got[1]
                    send(got[0])
                    sent = time.monotonic()
                    continue
                now = frames.current()  # the last frame of a burst gets its picture
                if now and now[1] <= seen and time.monotonic() - sent > 5:
                    send(now[0])
                    sent = time.monotonic()
        except (BrokenPipeError, ConnectionResetError, OSError):
            pass
        finally:
            frames.leave()


class FakeApp:
    """A made-up LiteBro on the made-up phone, so a tap, a text or a button visibly does something.

    One screen at a time: the start page with tiles, a page of a site, the system Home screen. In-app taps
    hit the boxes; system-wide finger events do nothing, as on the real phone (iOS 16 drops them). Every
    change of the screen is a short run of frames, as an animation is: the last one is the screen at rest.
    """

    TILES = [("Claude", "https://claude.ai/"), ("ChatGPT", "https://chatgpt.com/"), ("dsh", "http://192.168.1.10:3000/")]

    def __init__(self, phone, animate=True):
        self.phone = phone
        self.screen = "start"       # start | page | springboard
        self.url = ""
        self.field = ""             # the address field
        self.focus = False          # the address field has the keyboard
        self.down = None
        self.animate = animate
        self.run = 0
        self.lock = threading.Lock()
        phone.on_event = self.on_event
        phone.frames.truth = self.picture()
        phone.frames.pending = phone.frames.keeps

    # Boxes in 0…1 of the screen: x, y, w, h
    ADDRESS = (0.05, 0.90, 0.72, 0.06)
    GO = (0.80, 0.90, 0.15, 0.06)

    def tile_box(self, i):
        return (0.08 + 0.30 * i, 0.18, 0.24, 0.12)

    def own_picture(self):
        """What _remote/shot gives: LiteBro's own windows, whatever else the phone shows; None once it left the screen."""
        return None if self.screen == "springboard" else self.picture(size=(780, 1688))

    def picture(self, moving=None, size=(591, 1280)):
        extra = [] if moving is None else [(0.0, 0.45 + 0.01 * moving, 1.0, 0.03, "... moving ...", (120, 90, 20))]
        if self.screen == "springboard":
            return jpeg(["9:41", "", "Home screen of iOS", "(LiteBro is not on the screen)"], size=size, background=(10, 40, 80),
                        boxes=[(0.08, 0.30, 0.2, 0.1, "LiteBro", (90, 60, 160)), (0.38, 0.30, 0.2, 0.1, "TrollStore", (40, 120, 60))] + extra)
        boxes = [self.ADDRESS + (("| " if self.focus else "") + (self.field or "Search or address"), (50, 50, 60) if not self.focus else (30, 70, 120)),
                 self.GO + ("Go", (40, 110, 60))]
        if self.screen == "start":
            lines = ["LiteBro", "", "Tiles:"]
            for i, (name, _) in enumerate(self.TILES):
                boxes.append(self.tile_box(i) + (name, (90, 60, 160)))
        else:
            lines = ["Page: " + self.url, "", "The page of " + (urllib.parse.urlsplit(self.url).hostname or "?"), "loaded."]
        if self.focus:
            boxes.append((0.0, 0.62, 1.0, 0.26, "[ keyboard ]", (40, 40, 44)))
        return jpeg(lines, size=size, boxes=boxes + extra)

    def hit(self, x, y):
        """The class of the view under a point, as hitTest names it."""
        if self.screen == "springboard":
            return ""
        if self.inside(self.ADDRESS, x, y):
            return "UITextField"
        if self.inside(self.GO, x, y):
            return "UIButton"
        if self.screen == "start" and any(self.inside(self.tile_box(i), x, y) for i in range(len(self.TILES))):
            return "_UIHostingView"
        return "WKContentView" if self.screen == "page" else "_UIHostingView"

    def title(self):
        return "The page of " + (urllib.parse.urlsplit(self.url).hostname or "?")

    def script(self, code):
        """What page JavaScript gives back here: a few things a test asks a page for, else nothing."""
        code = code.strip()
        if "throw" in code:
            return {"error": "JavaScript exception: Error: thrown by the page"}
        for ask, value in (("document.title", self.title()), ("location.href", self.url),
                           ("document.body.innerText", self.title() + "\nloaded."), ("navigator.userAgent", "Mozilla/5.0 (iPhone; made up)")):
            if ask in code:
                return {"result": value}
        if code.startswith("return ") and code[7:].strip().rstrip(";").isdigit():
            return {"result": int(code[7:].strip().rstrip(";"))}
        return {"result": None}

    def changed(self):
        """The screen moves to its new look: iOS hands a frame about every 16 ms until it rests."""
        frames = self.phone.frames
        final = self.picture()
        self.run += 1
        if not self.animate:
            return frames.screen_changed(final)
        run, between = self.run, [self.picture(moving=i) for i in range(5)]

        def play():
            for p in between:
                if run != self.run:
                    return  # a newer change took over
                frames.screen_changed(p)
                time.sleep(0.016)
            if run == self.run:
                frames.screen_changed(final)

        threading.Thread(target=play, daemon=True).start()

    @staticmethod
    def inside(box, x, y):
        return box[0] <= x <= box[0] + box[2] and box[1] <= y <= box[1] + box[3]

    def go(self, text):
        text = text.strip()
        if not text:
            return
        self.url = text if "://" in text else "https://" + text
        self.screen, self.field, self.focus = "page", self.url, False

    def on_event(self, e):
        with self.lock:
            if e["kind"] == "button":
                if e["via"] == "app" and self.screen != "springboard":
                    if e["button"] == "home":
                        self.screen, self.focus = "springboard", False
                        self.changed()
                return
            if self.screen == "springboard" or e.get("via") != "app":
                return
            if e["kind"] == "open":
                self.go(e["url"])
                return self.changed()
            if e["kind"] == "click":
                if e["control"] == "UIButton" and self.field.strip():
                    self.go(self.field)  # the Go button got its action
                    self.changed()
                return
            if e["kind"] == "type":
                if not self.focus:
                    return
                if e["text"] == "\b":
                    self.field = self.field[:-1]
                elif "\n" in e["text"]:
                    self.field += e["text"].replace("\n", "")
                    self.go(self.field)
                else:
                    self.field += e["text"]
                return self.changed()
            if e["kind"] != "finger" or not e["inApp"] or self.phone.dead_taps:
                return
            if e["t"] == "down":
                self.down = (e["x"], e["y"])
                return
            if e["t"] != "up" or not self.down:
                return
            x, y = self.down
            self.down = None
            if abs(e["x"] - x) > 0.03 or abs(e["y"] - y) > 0.03:
                return  # a swipe: this app has nothing to scroll
            before = (self.screen, self.url, self.field, self.focus)
            if self.inside(self.ADDRESS, x, y):
                self.focus = True
                if self.screen == "page":
                    self.field = ""
            elif self.inside(self.GO, x, y):
                self.go(self.field)
            elif self.screen == "start":
                for i, (_, url) in enumerate(self.TILES):
                    if self.inside(self.tile_box(i), x, y):
                        self.go(url)
                        break
                else:
                    self.focus = False
            else:
                self.focus = False
            if before != (self.screen, self.url, self.field, self.focus):
                self.changed()  # a tap on nothing changes nothing


LOG = ("\n===== launch 2026-11-26 15:40:02 +0000 =====\n"
       "LiteBro 1.9.0 build 45363f3, iOS 16.2.0, iPhone14,5\n"
       "bundle /private/var/containers/Bundle/Application/6E0A0F0C-1111-4222-8333-444455556666/LiteBro.app\n"
       "data /var/mobile/Containers/Data/Application/0B9D7C2E-AAAA-4BBB-8CCC-DDDDEEEEFFFF\n"
       "sandbox: no (whole phone), free disk 48211 MB\n"
       "[0.01s 18 MB] debug ready\n[0.02s 19 MB] app init\n[0.09s 31 MB] browser init\n[0.10s 33 MB] tabs: 3\n"
       "[0.41s 52 MB] rule lists: 4\n[0.44s 55 MB] first screen\n[5.45s 61 MB] launch finished\n")
# A launch that died while the blocking rules were compiled, then a launch in the safe mode
LOG_CRASHED = ("\n===== launch 2026-11-26 16:02:10 +0000 =====\n"
               "LiteBro 1.9.0 build 45363f3, iOS 16.2.0, iPhone14,5\n"
               "bundle /private/var/containers/Bundle/Application/6E0A0F0C-1111-4222-8333-444455556666/LiteBro.app\n"
               "data /var/mobile/Containers/Data/Application/0B9D7C2E-AAAA-4BBB-8CCC-DDDDEEEEFFFF\n"
               "sandbox: no (whole phone), free disk 48190 MB\n"
               "[0.01s 18 MB] debug ready\n[0.02s 19 MB] app init\n[0.09s 31 MB] browser init\n[0.10s 33 MB] tabs: 3\n"
               "[0.12s 34 MB] rules …\nmemory warning, 412 MB\n"
               "\n===== launch 2026-11-26 16:02:31 +0000 =====\n"
               "LiteBro 1.9.0 build 45363f3, iOS 16.2.0, iPhone14,5\n"
               "bundle /private/var/containers/Bundle/Application/6E0A0F0C-1111-4222-8333-444455556666/LiteBro.app\n"
               "data /var/mobile/Containers/Data/Application/0B9D7C2E-AAAA-4BBB-8CCC-DDDDEEEEFFFF\n"
               "sandbox: no (whole phone), free disk 48188 MB\n"
               "!! the last launch did not finish, its last step: rules …\n"
               "SAFE MODE: no saved tabs, no filter lists, no rules\n"
               "[0.01s 18 MB] debug ready\n[0.02s 19 MB] app init\n[0.05s 24 MB] browser init\n[0.05s 24 MB] tabs: 1\n"
               "skipped rules: an earlier run died in it (Settings › Update lists runs it again)\n"
               "[0.06s 25 MB] rule lists: 0\n[0.09s 29 MB] first screen\n[5.10s 33 MB] launch finished\n")


def sample_tree(root, scene=None):
    """A little of what /var/mobile looks like through LiteBro.tipa."""
    def put(rel, data):
        p = os.path.join(root, *rel.split("/"))
        os.makedirs(os.path.dirname(p), exist_ok=True)
        with open(p, "wb") as f:
            f.write(data if isinstance(data, bytes) else data.encode("utf-8"))

    put("Media/DCIM/100APPLE/IMG_0001.JPG", jpeg(["photo 1"], size=(320, 240)))
    put("Media/DCIM/100APPLE/IMG_0002.JPG", jpeg(["photo 2"], size=(320, 240)))
    put("Media/DCIM/100APPLE/IMG_0003.MOV", os.urandom(300 * 1024))
    put("Media/Downloads/Отчёт за ноябрь.pdf", b"%PDF-1.4 made up\n")
    put("Media/LiteBro-Debug/log.txt", LOG + (LOG_CRASHED if scene == "crashed" else ""))
    app = "Containers/Data/Application/0B9D7C2E-AAAA-4BBB-8CCC-DDDDEEEEFFFF"
    meta = ".com.apple.mobile_container_manager.metadata.plist"
    put(app + "/" + meta, plistlib.dumps({"MCMMetadataIdentifier": "io.github.ronanime-arch.LiteBro", "MCMMetadataContentClass": 2},
                                         fmt=plistlib.FMT_BINARY))
    put(app + "/Library/Preferences/io.github.ronanime-arch.LiteBro.plist", plistlib.dumps({
        "tabs": ["https://claude.ai/", "", "http://192.168.1.10:3000/"], "currentTab": 2, "theme": "dark",
        "adBlock": True, "unloadAfter": 5}, fmt=plistlib.FMT_BINARY))
    other = "Containers/Data/Application/7A1E55D0-1234-4ABC-9DEF-0123456789AB"
    put(other + "/" + meta, plistlib.dumps({"MCMMetadataIdentifier": "com.apple.mobilenotes"}, fmt=plistlib.FMT_BINARY))
    put(other + "/Documents/note.txt", "made up\n")
    put(app + "/Documents/Downloads/video.mp4", os.urandom(64 * 1024))
    put(app + "/Documents/Debug/log.txt", "see Media/LiteBro-Debug/log.txt\n")
    if scene == "crashed":
        put(app + "/Documents/Debug/busy-rules.txt", "")
    put(app + "/Library/Application Support/tiles.json",
        json.dumps([{"name": "Claude", "url": "https://claude.ai/", "links": []}], ensure_ascii=False))
    put("Library/Logs/CrashReporter/LiteBro-2026-11-26-101500.ips", '{"app_name":"LiteBro","bug_type":"309"}\n')


def main():
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("--root", help="folder to serve as the phone's files (default: a made-up tree in a temp folder)")
    p.add_argument("--files-port", type=int, default=18080)
    p.add_argument("--screen-port", type=int, default=18081)
    p.add_argument("--files-token", help="the secret of the files address (default: random)")
    p.add_argument("--screen-token", help="the secret of the screen address (default: random)")
    p.add_argument("--host", default="127.0.0.1")
    p.add_argument("--no-control", action="store_true", help="«Управление касаниями» is off: taps answer 403")
    p.add_argument("--read-only", action="store_true", help="«Только чтение»")
    p.add_argument("--no-broadcast", action="store_true", help="the screen broadcast is not running")
    p.add_argument("--old-build", action="store_true", help="the build without GET frame: pictures only while watched")
    p.add_argument("--dead-taps", action="store_true", help="in-app fingers do nothing, as on the user's phone on 2026-11-27")
    p.add_argument("--magnifier", action="store_true", help="TrollStore's URL scheme is off: _install answers 409")
    p.add_argument("--scene", choices=["crashed"], help="what happened on the made-up phone before")
    p.add_argument("--idle", type=float, default=10.0, help="old build: seconds without a picture before the stream ends")
    p.add_argument("--log", help="append every request of the computer that acts to this file, one JSON per line")
    p.add_argument("--urls", help="write the two addresses to this file as JSON")
    a = p.parse_args()

    root = a.root
    if not root:
        root = tempfile.mkdtemp(prefix="mock-iphone-")
        sample_tree(root, a.scene)
    phone = MockPhone(root, a.files_port, a.screen_port, control=not a.no_control, read_only=a.read_only,
                      host=a.host, idle=a.idle, log=a.log, old_build=a.old_build,
                      files_token=a.files_token, screen_token=a.screen_token)
    phone.dead_taps = a.dead_taps
    phone.magnifier = a.magnifier
    FakeApp(phone)
    phone.start(broadcast=not a.no_broadcast)
    urls = {"files": phone.files_url, "screen": phone.screen_url, "root": root}
    if a.urls:
        with open(a.urls, "w", encoding="utf-8") as f:
            json.dump(urls, f)
    print("files   " + phone.files_url)
    print("screen  " + phone.screen_url)
    print("root    " + root, flush=True)
    try:
        while True:
            time.sleep(3600)
    except KeyboardInterrupt:
        phone.stop()


if __name__ == "__main__":
    main()
