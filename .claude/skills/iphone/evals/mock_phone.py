#!/usr/bin/env python3
"""A stand-in for the phone: the two servers LiteBro for iOS opens for a computer.

Mirrors ios/LiteBro/Phone/FileShare.swift (files, _touch, _install; port 8080) and
ios/Screen/SampleHandler.swift (pictures of the screen; port 8081) closely enough to test the helper
scripts and to rehearse a task without the phone. Keep it in step with those two files.

As a program it serves a small made-up phone until stopped:
    python mock_phone.py [--files-port N] [--screen-port N] [--no-control] [--read-only] [--log FILE]
"""
import argparse
import datetime
import email.utils
import io
import json
import mimetypes
import os
import plistlib
import secrets
import shutil
import sys
import tempfile
import threading
import time
import urllib.parse
import urllib.request
import uuid
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


def token():
    """MiniHTTP.token(): 16 random bytes, base64 for addresses."""
    return secrets.token_urlsafe(16)


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

    The phone encodes a picture only when the screen changes, only while somebody watches the stream
    and not more often than every 0.12 s. So the kept picture can be older than the screen.
    """

    def __init__(self, idle=10.0, gap=0.12):
        self.cond = threading.Condition()
        self.frame = None
        self.number = 0
        self.viewers = 0
        self.closed = False
        self.last = 0.0
        self.idle = idle
        self.gap = gap
        self.truth = None  # what the screen really shows, for the tests
        self.on_join = None  # called when somebody starts watching, for the tests

    def screen_changed(self, picture):
        """ReplayKit handed a frame. True when it was kept for the viewers."""
        with self.cond:
            self.truth = picture
            now = time.monotonic()
            if self.viewers <= 0 or now - self.last < self.gap:
                return False
            self.last = now
            self.frame = picture
            self.number += 1
            self.cond.notify_all()
            return True

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

    def next(self, after):
        """The picture after the one numbered `after`; None once closed or after `idle` s without one."""
        deadline = time.monotonic() + self.idle
        with self.cond:
            while self.number <= after and not self.closed:
                left = deadline - time.monotonic()
                if left <= 0 or not self.cond.wait(left):
                    if self.number <= after:
                        return None
            if self.closed or self.frame is None:
                return None
            return self.frame, self.number


class MockPhone:
    def __init__(self, root, files_port=0, screen_port=0, control=True, read_only=False,
                 host="127.0.0.1", idle=10.0, log=None):
        self.root = os.path.abspath(root)
        self.rootreal = os.path.realpath(self.root)
        self.host = host
        self.files_port = files_port
        self.screen_port = screen_port
        self.files_token = token()
        self.screen_token = token()
        self.control = control          # «Управление касаниями» with the screen permission still valid
        self.read_only = read_only      # «Только чтение»
        self.frames = FrameStore(idle=idle)
        self.events = []                # what the computer asked for, in order
        self.lock = threading.Lock()
        self.on_event = None            # a made-up app reacting to taps: callable(event)
        self.fetch_installs = True      # act as TrollStore: download what _install names
        self.home_suspends = True       # the Home button takes LiteBro off the screen
        self.log = log
        self._files = None
        self._screen = None

    # --- addresses ---

    @property
    def files_url(self):
        return "http://%s:%d/%s/" % (self.host, self.files_port, self.files_token)

    @property
    def screen_url(self):
        return "http://%s:%d/%s/" % (self.host, self.screen_port, self.screen_token)

    # --- life cycle ---

    def start(self, broadcast=True):
        self.resume()
        if broadcast:
            self.start_broadcast()
        return self

    def stop(self):
        self.suspend()
        self.stop_broadcast()

    def resume(self):
        """LiteBro on the screen with file access open: the files server answers."""
        if self._files:
            return
        self._files = self._serve(FilesHandler, self.files_port)
        self.files_port = self._files.server_address[1]

    def suspend(self):
        """LiteBro left the screen (or access was closed): nothing answers on the files port."""
        s, self._files = self._files, None
        if s:
            s.shutdown()
            s.server_close()

    def start_broadcast(self):
        if self._screen:
            return
        self.frames.closed = False
        self._screen = self._serve(ScreenHandler, self.screen_port)
        self.screen_port = self._screen.server_address[1]

    def stop_broadcast(self):
        s, self._screen = self._screen, None
        if s:
            self.frames.close()
            s.shutdown()
            s.server_close()

    def _serve(self, handler, port):
        server = ThreadingHTTPServer(("0.0.0.0" if self.host != "127.0.0.1" else "127.0.0.1", port), handler)
        server.daemon_threads = True
        server.phone = self
        threading.Thread(target=server.serve_forever, kwargs={"poll_interval": 0.05}, daemon=True).start()
        return server

    # --- what happened ---

    def event(self, e):
        e["at"] = round(time.time(), 3)
        with self.lock:
            self.events.append(e)
        if self.log:
            with open(self.log, "a", encoding="utf-8") as f:
                f.write(json.dumps(e, ensure_ascii=False) + "\n")
        if self.on_event:
            try:
                self.on_event(e)
            except Exception as error:  # the made-up app must not take the server down
                sys.stderr.write("mock app: %r\n" % (error,))
        if e["kind"] == "button" and e["button"] == "home" and e.get("via") == "app" and self.home_suspends:
            threading.Timer(0.3, self.suspend).start()
        if e["kind"] == "install" and self.fetch_installs:
            threading.Thread(target=self._install, args=(e,), daemon=True).start()

    def _install(self, e):
        """TrollStore downloads the build; LiteBro is closed for the install."""
        try:
            with urllib.request.urlopen(e["url"], timeout=30) as r:
                data = r.read()
            e["downloaded"] = len(data)
            e["data"] = data
        except Exception as error:
            e["error"] = repr(error)
        if self.log:
            with open(self.log, "a", encoding="utf-8") as f:
                f.write(json.dumps({"kind": "installed", "bytes": e.get("downloaded"), "error": e.get("error")}) + "\n")
        self.suspend()

    def take_events(self):
        with self.lock:
            out, self.events = self.events, []
        return out

    # --- files ---

    def resolve(self, uri):
        """FileShare.file(_:): the file of a request path, only inside the shared folder."""
        path = uri.split("?", 1)[0]
        parts = [urllib.parse.unquote(p) for p in path.split("/") if p]
        if not parts or parts[0] != self.files_token:
            return None
        parts = parts[1:]
        if any(p in ("..", ".") or "\0" in p for p in parts):
            return None
        full = os.path.join(self.root, *parts) if parts else self.root
        real = os.path.realpath(full)
        if os.path.lexists(full) and not (real == self.rootreal or real.startswith(self.rootreal + os.sep)):
            return None
        return full, parts

    def href(self, rel, is_dir):
        enc = [urllib.parse.quote(p, safe="-._~:") for p in rel]
        return "/" + "/".join([self.files_token] + enc) + ("/" if (is_dir or not rel) else "")


def xml_escape(s):
    return s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace('"', "&quot;")


REASONS = {100: "Continue", 200: "OK", 201: "Created", 202: "Status", 204: "No Content", 206: "Partial Content",
           207: "Multi-Status", 301: "Moved Permanently", 400: "Bad Request", 403: "Forbidden", 404: "Not Found",
           405: "Method Not Allowed", 409: "Conflict", 412: "Precondition Failed", 416: "Range Not Satisfiable"}


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *a):
        pass

    def respond(self, status, headers=(), body=None, head_only=False):
        self.send_response(status, REASONS.get(status, "Status"))
        for n, v in headers:
            self.send_header(n, v)
        self.send_header("Content-Length", str(len(body) if body else 0))
        self.end_headers()
        if body and not head_only:
            self.wfile.write(body)

    def read_body(self, sink=None, limit=1 << 20):
        left = int(self.headers.get("Content-Length") or 0)
        body = b""
        while left > 0:
            piece = self.rfile.read(min(left, 256 * 1024))
            if not piece:
                break
            left -= len(piece)
            if sink:
                sink.write(piece)
            elif len(body) < limit:
                body += piece
        return body

    def json_body(self, body):
        try:
            m = json.loads(body.decode("utf-8"))
        except (ValueError, UnicodeDecodeError):
            return None
        return m if isinstance(m, dict) else None


def number(v):
    return isinstance(v, (int, float)) and not isinstance(v, bool)


OPTION_TYPES = {"client": str, "sender": str, "bks": bool, "points": bool, "parentXY": bool, "index": int,
                "identity": int, "builtIn": bool, "pressure": (int, float), "inApp": bool}


def touch_event(m, via):
    """What a touch request means to Touch.swift, or None for JSON it ignores (still answered 204)."""
    if isinstance(m.get("button"), str):
        return {"kind": "button", "button": "side" if m["button"] == "side" else "home", "via": via}
    if isinstance(m.get("type"), str) and via == "app":
        return {"kind": "type", "text": m["type"], "via": via}
    if m.get("t") in ("down", "move", "up") and number(m.get("x")) and number(m.get("y")):
        opts = {k: m[k] for k, t in OPTION_TYPES.items() if isinstance(m.get(k), t)}
        return {"kind": "finger", "t": m["t"], "x": min(max(float(m["x"]), 0.0), 1.0),
                "y": min(max(float(m["y"]), 0.0), 1.0), "inApp": opts.get("inApp") is True, "opts": opts, "via": via}
    return None


class FilesHandler(Handler):
    def handle_any(self):
        ph = self.server.phone
        method, uri = self.command, self.path
        path = uri.split("?", 1)[0]
        f = ph.resolve(uri)
        upload = None
        if method == "PUT" and not ph.read_only and f:
            temp = os.path.join(os.path.dirname(f[0]), ".litebro-upload-" + uuid.uuid4().hex)
            try:
                upload = (open(temp, "wb"), temp, f[0])
            except OSError:
                upload = None
        body = self.read_body(upload[0] if upload else None)
        if upload:
            upload[0].close()
        t = ph.files_token

        def drop_upload():
            if upload and os.path.exists(upload[1]):
                os.remove(upload[1])

        if path == "/%s/_install" % t and method == "POST":
            drop_upload()
            m = self.json_body(body)
            url = m.get("url") if m else None
            if not (ph.control and isinstance(url, str) and urllib.parse.urlsplit(url).scheme in ("http", "https")):
                return self.respond(403)
            self.respond(202)
            return ph.event({"kind": "install", "url": url})
        if path == "/%s/_touch" % t:
            drop_upload()
            if not ph.control:
                return self.respond(403)
            m = self.json_body(body) if method == "POST" else None
            if m is not None:
                self.respond(204)
                e = touch_event(m, "app")
                return ph.event(e) if e else None
            report = b'{"available":true,"simple":true,"withType":true,"bks":true}'
            return self.respond(200, [("Content-Type", "application/json")], report, head_only=method == "HEAD")
        if f is None:
            drop_upload()
            return self.respond(404)
        full, rel = f
        if ph.read_only and method in ("PUT", "DELETE", "MKCOL", "MOVE", "COPY", "PROPPATCH"):
            drop_upload()
            return self.respond(403)
        exists = os.path.lexists(full)
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
                        xml += self.entry(os.path.join(full, n), rel + [n])
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
                return self.respond(200, [("Content-Type", "text/html; charset=utf-8"), ("Cache-Control", "no-store")],
                                    page, head_only=method == "HEAD")
            return self.send_file(full, method)
        if method == "PUT":
            if not upload:
                return self.respond(409)
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
            to = ph.resolve(urllib.parse.urlsplit(d).path) if d else None
            if not exists or not rel or not to:
                return self.respond(400)
            overwrite = (self.headers.get("Overwrite") or "").upper() != "F"
            had = os.path.lexists(to[0])
            if had and not overwrite:
                return self.respond(412)
            try:
                if had:
                    shutil.rmtree(to[0]) if os.path.isdir(to[0]) else os.remove(to[0])
                if method == "MOVE":
                    os.rename(full, to[0])
                elif is_dir:
                    shutil.copytree(full, to[0])
                else:
                    shutil.copyfile(full, to[0])
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

    do_GET = do_HEAD = do_PUT = do_DELETE = do_POST = do_OPTIONS = handle_any
    do_PROPFIND = do_PROPPATCH = do_MKCOL = do_MOVE = do_COPY = do_LOCK = do_UNLOCK = handle_any

    def entry(self, full, rel):
        ph = self.server.phone
        try:
            st = os.stat(full)
        except OSError:
            st = None
        is_dir = os.path.isdir(full)
        mod = st.st_mtime if st else time.time()
        x = "<D:response><D:href>" + xml_escape(ph.href(rel, is_dir)) + "</D:href><D:propstat><D:prop>"
        x += "<D:displayname>" + xml_escape(rel[-1] if rel else "") + "</D:displayname>"
        if is_dir:
            x += "<D:resourcetype><D:collection/></D:resourcetype>"
        else:
            kind = mimetypes.guess_type(full)[0] or "application/octet-stream"
            x += "<D:resourcetype/><D:getcontentlength>%d</D:getcontentlength><D:getcontenttype>%s</D:getcontenttype>" % (
                st.st_size if st else 0, kind)
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
            self.send_header("Content-Type", mimetypes.guess_type(full)[0] or "application/octet-stream")
            self.send_header("Content-Length", str(length))
            self.send_header("Accept-Ranges", "bytes")
            self.send_header("Last-Modified", email.utils.formatdate(st.st_mtime, usegmt=True))
            if partial:
                self.send_header("Content-Range", "bytes %d-%d/%d" % (start, end, size))
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
        rows = ""
        for n in sorted(os.listdir(full)):
            if n.startswith(".litebro-upload-"):
                continue
            is_dir = os.path.isdir(os.path.join(full, n))
            rows += '<tr><td><a href="%s">%s%s</a></td></tr>' % (ph.href(rel + [n], is_dir), "📁 " if is_dir else "", xml_escape(n))
        return "<!doctype html><html><head><meta charset=utf-8><title>Телефон</title></head><body><table>%s</table></body></html>" % rows


class ScreenHandler(Handler):
    def handle_any(self):
        ph = self.server.phone
        path = self.path.split("?", 1)[0]
        body = self.read_body(limit=4096)
        t = ph.screen_token
        if path in ("/%s/" % t, "/%s" % t):
            page = ('<!doctype html><html><head><meta charset=utf-8><title>Экран телефона</title></head><body>'
                    '<img src="stream" alt="">%s</body></html>' % ("<div id=bar></div>" if ph.control else ""))
            return self.respond(200, [("Content-Type", "text/html; charset=utf-8"), ("Cache-Control", "no-store")],
                                page.encode("utf-8"), head_only=self.command == "HEAD")
        if path == "/%s/stream" % t:
            return self.stream()
        if path == "/%s/touch" % t and self.command == "POST":
            m = self.json_body(body)
            if not ph.control or m is None:
                return self.respond(403)
            self.respond(204)
            e = touch_event(m, "screen")  # the extension's sandbox keeps it from the HID system: no effect
            return ph.event(e) if e else None
        return self.respond(404)

    do_GET = do_HEAD = do_POST = do_OPTIONS = do_PUT = do_DELETE = do_PROPFIND = handle_any

    def stream(self):
        """multipart/x-mixed-replace: the kept picture at once, then each new one; ends after `idle` s of nothing."""
        frames = self.server.phone.frames
        boundary = "litebroframe"
        self.send_response(200)
        self.send_header("Content-Type", "multipart/x-mixed-replace; boundary=" + boundary)
        self.send_header("Cache-Control", "no-store")
        self.send_header("Connection", "close")
        self.end_headers()
        self.close_connection = True
        frames.join()
        try:
            seen = 0
            while True:
                got = frames.next(seen)
                if not got:
                    break
                picture, seen = got
                part = ("--%s\r\nContent-Type: image/jpeg\r\nContent-Length: %d\r\n\r\n" % (boundary, len(picture))).encode()
                self.wfile.write(part + picture + b"\r\n")
                self.wfile.flush()
        except (BrokenPipeError, ConnectionResetError, OSError):
            pass
        finally:
            frames.leave()


class FakeApp:
    """A made-up LiteBro on the made-up phone, so a tap, a text or a button visibly does something.

    One screen at a time: the start page with tiles, a page of a site, the system Home screen. In-app taps
    hit the boxes; system-wide finger events do nothing, as on the real phone (iOS 16 drops them).
    """

    TILES = [("Claude", "https://claude.ai/"), ("ChatGPT", "https://chatgpt.com/"), ("dsh", "http://192.168.1.10:3000/")]

    def __init__(self, phone):
        self.phone = phone
        self.screen = "start"       # start | page | springboard
        self.url = ""
        self.field = ""             # the address field
        self.focus = False          # the address field has the keyboard
        self.down = None
        self.lock = threading.Lock()
        phone.on_event = self.on_event
        phone.frames.truth = self.picture()

    # Boxes in 0…1 of the screen: x, y, w, h
    ADDRESS = (0.05, 0.90, 0.72, 0.06)
    GO = (0.80, 0.90, 0.15, 0.06)

    def tile_box(self, i):
        return (0.08 + 0.30 * i, 0.18, 0.24, 0.12)

    def picture(self):
        if self.screen == "springboard":
            return jpeg(["9:41", "", "Home screen of iOS", "(LiteBro is not on the screen)"], background=(10, 40, 80),
                        boxes=[(0.08, 0.30, 0.2, 0.1, "LiteBro", (90, 60, 160)), (0.38, 0.30, 0.2, 0.1, "TrollStore", (40, 120, 60))])
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
        return jpeg(lines, boxes=boxes)

    def changed(self):
        self.phone.frames.screen_changed(self.picture())

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
                if e["via"] == "app" and e["button"] == "home" and self.screen != "springboard":
                    self.screen, self.focus = "springboard", False
                    self.changed()
                return
            if self.screen == "springboard" or e.get("via") != "app":
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
            if e["kind"] != "finger" or not e["inApp"]:
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
            self.changed()


def sample_tree(root):
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
    put("Media/LiteBro-Debug/log.txt",
        "\n===== launch 2026-11-26 15:40:02 +0000 =====\n"
        "LiteBro 1.9.0 build 45363f3, iOS 16.2.0, iPhone14,5\n"
        "bundle /private/var/containers/Bundle/Application/6E0A0F0C-1111-4222-8333-444455556666/LiteBro.app\n"
        "data /var/mobile/Containers/Data/Application/0B9D7C2E-AAAA-4BBB-8CCC-DDDDEEEEFFFF\n"
        "sandbox: no (whole phone), free disk 48211 MB\n"
        "[0.01s 18 MB] debug ready\n[0.02s 19 MB] app init\n[0.09s 31 MB] browser init\n[0.10s 33 MB] tabs: 3\n"
        "[0.41s 52 MB] rule lists: 4\n[0.44s 55 MB] first screen\n[5.45s 61 MB] launch finished\n")
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
    put(app + "/Library/Application Support/tiles.json",
        json.dumps([{"name": "Claude", "url": "https://claude.ai/", "links": []}], ensure_ascii=False))
    put("Library/Logs/CrashReporter/LiteBro-2026-11-26-101500.ips", '{"app_name":"LiteBro","bug_type":"309"}\n')


def main():
    p = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    p.add_argument("--root", help="folder to serve as the phone's files (default: a made-up tree in a temp folder)")
    p.add_argument("--files-port", type=int, default=18080)
    p.add_argument("--screen-port", type=int, default=18081)
    p.add_argument("--host", default="127.0.0.1")
    p.add_argument("--no-control", action="store_true", help="«Управление касаниями» is off: taps answer 403")
    p.add_argument("--read-only", action="store_true", help="«Только чтение»")
    p.add_argument("--no-broadcast", action="store_true", help="the screen broadcast is not running")
    p.add_argument("--idle", type=float, default=10.0, help="seconds without a new picture before the stream ends")
    p.add_argument("--log", help="append every request of the computer to this file, one JSON per line")
    p.add_argument("--urls", help="write the two addresses to this file as JSON")
    a = p.parse_args()

    root = a.root
    if not root:
        root = tempfile.mkdtemp(prefix="mock-iphone-")
        sample_tree(root)
    phone = MockPhone(root, a.files_port, a.screen_port, control=not a.no_control, read_only=a.read_only,
                      host=a.host, idle=a.idle, log=a.log)
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
