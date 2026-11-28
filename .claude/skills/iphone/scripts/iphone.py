#!/usr/bin/env python3
# iphone.py - the user's iPhone from a computer in the same Wi-Fi, through LiteBro for iOS
# ("Access from PC"): files, pictures of the screen, taps and text, builds.
# Python 3.8+, no packages. The same commands and output as iphone.ps1 beside it.
import base64
import datetime
import fnmatch
import http.client
import json
import os
import plistlib
import select
import socket
import sys
import tempfile
import time
import unicodedata
import urllib.parse
import xml.etree.ElementTree as ET

HELP = """iphone - the user's iPhone through LiteBro for iOS. Addresses come from the phone: LiteBro,
menu "...", "Access from PC" (files: port 8080 or 8082-8089, screen: port 8081).

  connect <url> [<url>]        remember the files and/or screen address and check them
                               (or --files <url> --screen <url> to say which is which)
  status                       what answers now: files, taps, screen, the app's build
  wait [files|screen] [--seconds 120]      until the address answers
  forget                       drop the remembered addresses

  ls [path] [--json]           a folder of the phone (paths start at the shared root)
  tree [path] [--depth 2] [--max 1000]
  find <path> <pattern> [--depth 6] [--max 200]     names by a wildcard, e.g. "*.mov"
  du [path]                    sizes of what is inside
  cat <path> [--tail N] [--head N]        a text file
  plist <path>                 a property list (binary too) as JSON
  apps [filter] [--groups]     data folders of the apps with their bundle ids
  get <remote> [local] [-r]    download a file (a folder with -r)
  put <local> <remote> [-r]    upload
  mkdir <remote>   rm <remote> [-r]   mv <from> <to> [--force]   cp <from> <to> [--force]

  shot [file] [--wait 12]      a picture of the phone's screen (needs the broadcast)
  shot [file] --app            a picture of LiteBro's own screen, made by the app: no broadcast needed
                               (shot alone gives it too when the screen address is not set or does not answer)
  watch [dir] [--seconds 10] [--until-change]       pictures as the screen changes

  page [--json]                what LiteBro shows: the address, the title, whether it is loading
  open <address> [--new-tab] [--wait 20]   open an address in LiteBro on the phone
  js <code> | --file <file> [--timeout 20] JavaScript in the page: the body of an async function, its return comes back
  click <x> <y>                press a button or a switch of LiteBro itself at a point (not what a page draws)
  remote <name> [json | key=value ...]     any other test command of the phone, as it is

  tap <x> <y>                  a tap inside LiteBro; x y are 0..1 or pixels of the last shot
  hold <x> <y> [--ms 800]      a long press
  swipe <x1> <y1> <x2> <y2> [--ms 300] [--steps 12]
  type <text> [--enter]        text into the focused field of LiteBro (--file <file>: any text)
  key backspace|enter [--count N]
  button home|side --yes       a button of the phone (takes LiteBro off the screen)
  touch [json | key=value ...] [--via app|screen]    the raw request; alone: what the phone can do
    for tap, hold, swipe, type, key, click, button: --shot [--out file] looks at the screen after the action
    (without the screen address, or with --app: at LiteBro's own screen; a button needs the screen address);
    for tap, hold, swipe: --system sends a system-wide finger, --opt key=value passes an option of the
    touch request (recipe, masks, sender, ...), --of WxH says what the pixels are of

  log [--tail 60] [--launch]   LiteBro's own log on the phone (--launch: the last launch, and the one before
                               it when that one did not finish)
  install                      TrollStore installs LiteBro's released build (the "ios" release): the whole-phone
                               LiteBro.tipa; over the Browser build only with --whole-phone
  install <file.tipa>          put another build into LiteBro's folder on the phone, to open with TrollStore by hand

Exit codes: 0 done, 1 wrong use, 2 the phone does not answer, 3 the phone refused (403),
4 not found (404), 5 no picture."""

STATE = os.environ.get("LITEBRO_IPHONE_STATE") or os.path.join(os.path.expanduser("~"), ".litebro-iphone.json")
try:
    TIMEOUT = float(os.environ.get("LITEBRO_IPHONE_TIMEOUT") or 8)  # seconds to wait for an answer
except ValueError:
    TIMEOUT = 8.0
REFUSED = "refused (nothing listens there: access is closed, its time is over, or the broadcast is not running)"
SILENT = "timeout (LiteBro is not on the screen, or the phone is asleep, in another network, or the address is wrong)"
# The one address LiteBro on the phone takes for an install asked from a computer (FileShare.ownBuild)
OWN_BUILD = "https://github.com/ronanime-arch/LiteBro/releases/download/ios/LiteBro-PC-install.tipa"
LOGS = ["Media/LiteBro-Debug/log.txt", "Debug/log.txt"]


class Fail(Exception):
    def __init__(self, code, text, refused=False):
        Exception.__init__(self, text)
        self.code = code
        self.refused = refused  # nothing listens at the address, while the phone itself is there


def out(text=""):
    sys.stdout.write(text + "\n")
    sys.stdout.flush()


def note(text):
    sys.stderr.write(text + "\n")
    sys.stderr.flush()


# --- arguments ---

def parse(argv, flags=(), options=(), many=()):
    """Positional arguments, --flags without a value, --options with one, --many that repeat."""
    pos, opt = [], {}
    i = 0
    while i < len(argv):
        a = argv[i]
        name = a[2:] if a.startswith("--") else ("recursive" if a == "-r" else None)
        if name is None or a == "--":
            pos.append(a)
        elif name in flags:
            opt[name] = True
        elif name in options or name in many:
            if i + 1 >= len(argv):
                raise Fail(1, "--%s needs a value" % name)
            i += 1
            if name in many:
                opt.setdefault(name, []).append(argv[i])
            else:
                opt[name] = argv[i]
        else:
            raise Fail(1, "unknown option %s (see: iphone help)" % a)
        i += 1
    return pos, opt


def number(opt, name, default, kind=float):
    if name not in opt:
        return default
    try:
        return kind(opt[name])
    except ValueError:
        raise Fail(1, "--%s takes a number" % name)


# --- the remembered addresses ---

def load_state():
    try:
        with open(STATE, "r", encoding="utf-8") as f:
            s = json.load(f)
        return s if isinstance(s, dict) else {}
    except (OSError, ValueError):
        return {}


def save_state(s):
    with open(STATE, "w", encoding="utf-8") as f:
        json.dump(s, f, indent=1)
    try:
        os.chmod(STATE, 0o600)  # the address is the password
    except OSError:
        pass


class Server:
    """One of the phone's two addresses: http://host:port/<secret>/"""

    def __init__(self, url, timeout=None):
        u = urllib.parse.urlsplit(url.strip())
        if u.scheme != "http" or not u.hostname or not u.path.strip("/"):
            raise Fail(1, "not an address of the phone: %s (it looks like http://192.168.1.23:8080/<secret>/)" % url)
        self.host = u.hostname
        self.port = u.port or 80
        self.base = "/" + u.path.strip("/").split("/")[0] + "/"  # the secret; a pasted .../stream still works
        self.url = "http://%s:%d%s" % (self.host, self.port, self.base)
        self.timeout = min(timeout, TIMEOUT) if timeout else TIMEOUT
        self.conn = None

    def path(self, rel):
        parts = [p for p in rel.replace("\\", "/").split("/") if p]
        return self.base + "/".join(urllib.parse.quote(p, safe="") for p in parts)

    def close(self):
        if self.conn:
            try:
                self.conn.close()
            except OSError:
                pass
        self.conn = None

    def open(self, method, path, body=None, headers=None, timeout=None):
        """Sends a request and returns the open answer; a connection the phone closed meanwhile is made again."""
        for attempt in (0, 1):
            fresh = self.conn is None
            try:
                if fresh:
                    self.conn = http.client.HTTPConnection(self.host, self.port, timeout=timeout or self.timeout)
                elif self.conn.sock:
                    self.conn.sock.settimeout(timeout or self.timeout)
                if hasattr(body, "seek"):
                    body.seek(0)
                self.conn.request(method, path, body=body, headers=headers or {})
                return self.conn.getresponse()
            except ConnectionRefusedError:
                self.close()
                raise Fail(2, "no answer from %s:%d: %s" % (self.host, self.port, REFUSED), refused=True)
            except (socket.timeout, TimeoutError):
                self.close()
                raise Fail(2, "no answer from %s:%d: %s" % (self.host, self.port, SILENT))
            except (http.client.HTTPException, OSError) as e:
                self.close()
                if fresh or attempt:
                    raise Fail(2, "no answer from %s:%d: %s" % (self.host, self.port, e))

    def request(self, method, path, body=None, headers=None, timeout=None):
        r = self.open(method, path, body, headers, timeout)
        try:
            data = r.read()
        except (http.client.HTTPException, OSError) as e:
            self.close()
            raise Fail(2, "the answer from %s:%d broke off: %s" % (self.host, self.port, e))
        if r.will_close:
            self.close()
        return r.status, r, data


def nfc(name):
    """A name from the phone in the form people type: iOS stores names decomposed, and finds them in either form."""
    return unicodedata.normalize("NFC", name)


def need(kind):
    url = load_state().get(kind)
    if not url:
        raise Fail(1, "no %s address yet: iphone connect <address from the phone>" % kind)
    return Server(url)


def check(status, what, ok=(200, 201, 204, 206, 207)):
    if status in ok:
        return
    if status == 403:
        raise Fail(3, "%s: the phone refused (403)" % what)
    if status == 404:
        raise Fail(4, "%s: not found (404)" % what)
    raise Fail(1, "%s: the phone answered %d" % (what, status))


# --- files ---

DAV = "{DAV:}"


def propfind(server, rel, depth=1):
    """The folder itself first, then what is inside: name, dir, size, modified (UTC, ISO)."""
    status, _, data = server.request("PROPFIND", server.path(rel), headers={"Depth": str(depth)})
    check(status, rel or "/")
    try:
        root = ET.fromstring(data)
    except ET.ParseError:
        raise Fail(1, "the phone's answer for %s is not a WebDAV listing" % (rel or "/"))
    entries = []
    for r in root.findall(DAV + "response"):
        href = r.findtext(DAV + "href") or ""
        prop = r.find(DAV + "propstat/" + DAV + "prop")
        if prop is None:
            continue
        parts = [p for p in urllib.parse.urlsplit(href).path.split("/") if p]
        name = nfc(urllib.parse.unquote(parts[-1])) if len(parts) > 1 else ""
        is_dir = prop.find(DAV + "resourcetype/" + DAV + "collection") is not None
        size = int(prop.findtext(DAV + "getcontentlength") or 0)
        modified = ""
        text = prop.findtext(DAV + "getlastmodified")
        if text:
            try:
                modified = datetime.datetime.strptime(text, "%a, %d %b %Y %H:%M:%S GMT").strftime("%Y-%m-%dT%H:%M:%SZ")
            except ValueError:
                modified = ""
        entries.append({"name": name, "dir": is_dir, "size": 0 if is_dir else size, "modified": modified})
    if not entries:
        raise Fail(1, "the phone's listing of %s is empty" % (rel or "/"))
    return entries[0], sorted(entries[1:], key=lambda e: (not e["dir"], e["name"].lower(), e["name"]))


def join(a, b):
    return (a.strip("/") + "/" + b).strip("/")


def human(n):
    for unit in ("B", "KB", "MB", "GB"):
        if n < 1024 or unit == "GB":
            return ("%d %s" % (n, unit)) if unit == "B" else ("%.1f %s" % (n, unit))
        n /= 1024.0


def line(e):
    if e["dir"]:
        return "d %12s  %s  %s/" % ("-", e["modified"] or "-", e["name"])
    return "f %12d  %s  %s" % (e["size"], e["modified"] or "-", e["name"])


def cmd_ls(argv):
    pos, opt = parse(argv, flags=("json",))
    rel = pos[0] if pos else ""
    me, inside = propfind(need("files"), rel)
    if not me["dir"]:
        inside = [me]
    if "json" in opt:
        return out(json.dumps(inside))
    for e in inside:
        out(line(e))
    if not inside:
        out("(empty)")


def walk(server, rel, depth, visit, unreadable):
    """Folders first to last, `depth` levels down; visit(path, entry) returns False to stop everything."""
    try:
        _, inside = propfind(server, rel)
    except Fail as e:
        if e.code in (3, 4):
            unreadable.append(rel)
            return True
        raise
    for e in inside:
        p = join(rel, e["name"])
        if visit(p, e) is False:
            return False
        if e["dir"] and depth > 1 and not walk(server, p, depth - 1, visit, unreadable):
            return False
    return True


def cmd_tree(argv):
    pos, opt = parse(argv, options=("depth", "max"))
    rel = pos[0] if pos else ""
    depth, most = number(opt, "depth", 2, int), number(opt, "max", 1000, int)
    count, unreadable = [0], []
    base = len([p for p in rel.split("/") if p])

    def visit(p, e):
        if count[0] >= most:
            return False
        count[0] += 1
        level = len(p.split("/")) - base - 1
        out("  " * level + e["name"] + ("/" if e["dir"] else "  (%s)" % human(e["size"])))

    done = walk(need("files"), rel, depth, visit, unreadable)
    if not done:
        out("... stopped at %d entries (--max)" % most)
    for u in unreadable:
        note("not readable: " + u)


def cmd_find(argv):
    pos, opt = parse(argv, options=("depth", "max"))
    if len(pos) != 2:
        raise Fail(1, "find <path> <pattern>")
    depth, most = number(opt, "depth", 6, int), number(opt, "max", 200, int)
    pattern = nfc(pos[1]).lower()
    found, unreadable = [0], []

    def visit(p, e):
        if fnmatch.fnmatchcase(e["name"].lower(), pattern):
            if found[0] >= most:
                return False
            found[0] += 1
            out(p + ("/" if e["dir"] else "\t%d" % e["size"]))

    done = walk(need("files"), pos[0], depth, visit, unreadable)
    if not done:
        out("... stopped at %d matches (--max)" % most)
    elif not found[0]:
        out("(nothing)")
    for u in unreadable:
        note("not readable: " + u)


def cmd_du(argv):
    pos, opt = parse(argv, options=("max",))
    rel = pos[0] if pos else ""
    most = number(opt, "max", 20000, int)
    server = need("files")
    _, inside = propfind(server, rel)
    seen, unreadable, rows, total = [0], [], [], 0
    for e in inside:
        size = [e["size"]]
        if e["dir"]:
            def visit(p, x):
                seen[0] += 1
                size[0] += x["size"]
                return seen[0] < most
            walk(server, join(rel, e["name"]), 64, visit, unreadable)
        rows.append((size[0], e["name"] + ("/" if e["dir"] else "")))
        total += size[0]
    for size, name in sorted(rows, key=lambda r: (-r[0], r[1])):
        out("%12d  %10s  %s" % (size, human(size), name))
    out("%12d  %10s  total" % (total, human(total)))
    if seen[0] >= most:
        out("... stopped after %d entries (--max): sizes are a lower bound" % most)
    for u in unreadable:
        note("not readable: " + u)


def fetch(server, rel, headers=None):
    status, r, data = server.request("GET", server.path(rel), headers=headers)
    if status == 301:
        raise Fail(1, "%s is a folder" % rel)
    check(status, rel)
    return data, r


def text_of(data, rel):
    if b"\0" in data[:4096]:
        raise Fail(1, "%s is not text (download it with get, or read a property list with plist)" % rel)
    return data.decode("utf-8", "replace")


def cmd_cat(argv):
    pos, opt = parse(argv, options=("tail", "head"))
    if len(pos) != 1:
        raise Fail(1, "cat <path> [--tail N] [--head N]")
    server = need("files")
    limit = 256 * 1024
    if "tail" in opt:
        n = number(opt, "tail", 60, int)
        data, _ = fetch(server, pos[0], {"Range": "bytes=-%d" % limit})
        lines = text_of(data, pos[0]).splitlines()
        return out("\n".join(lines[max(0, len(lines) - n):]))
    data, r = fetch(server, pos[0], {"Range": "bytes=0-%d" % (limit - 1)})
    text = text_of(data, pos[0])
    if "head" in opt:
        return out("\n".join(text.splitlines()[:max(0, number(opt, "head", 60, int))]))
    out(text.rstrip("\n"))
    whole = (r.getheader("Content-Range") or "").rpartition("/")[2]
    if whole.isdigit() and int(whole) > len(data):
        note("... the first %d of %s bytes (use --tail, or get the file)" % (len(data), whole))


def plain(v):
    """A property list value as JSON can hold it."""
    if isinstance(v, dict):
        return {str(k): plain(x) for k, x in v.items()}
    if isinstance(v, (list, tuple)):
        return [plain(x) for x in v]
    if isinstance(v, (bytes, bytearray)):
        return {"data": base64.b64encode(bytes(v)).decode("ascii")}
    if isinstance(v, datetime.datetime):
        return v.strftime("%Y-%m-%dT%H:%M:%SZ")
    if isinstance(v, plistlib.UID):
        return {"uid": v.data}
    return v


def read_plist(server, rel):
    data, _ = fetch(server, rel)
    try:
        return plain(plistlib.loads(data))
    except Exception:
        raise Fail(1, "%s is not a property list" % rel)


def cmd_plist(argv):
    pos, _ = parse(argv)
    if len(pos) != 1:
        raise Fail(1, "plist <path>")
    out(json.dumps(read_plist(need("files"), pos[0]), indent=1, sort_keys=True))


def cmd_apps(argv):
    pos, opt = parse(argv, flags=("groups",))
    wanted = pos[0].lower() if pos else ""
    server = need("files")
    base = "Containers/Shared/AppGroup" if "groups" in opt else "Containers/Data/Application"
    try:
        _, inside = propfind(server, base)
    except Fail as e:
        if e.code == 4:
            raise Fail(4, "%s is not there: this build shares only LiteBro's own folder (LiteBro-Browser.tipa)" % base)
        raise
    rows = []
    for e in inside:
        if not e["dir"]:
            continue
        p = join(base, e["name"])
        try:
            name = read_plist(server, p + "/.com.apple.mobile_container_manager.metadata.plist").get("MCMMetadataIdentifier")
        except Fail:
            name = None
        name = name if isinstance(name, str) else "?"
        if wanted in name.lower():
            rows.append((name.lower(), name, p))
    for _, name, p in sorted(rows):
        out("%s\t%s" % (name, p))
    if not rows:
        out("(nothing)")


def download(server, rel, local):
    r = server.open("GET", server.path(rel), timeout=30)
    if r.status != 200:
        r.read()
        if r.status == 301:
            raise Fail(1, "%s is a folder: add -r" % rel)
        check(r.status, rel)
    size = 0
    try:
        with open(local, "wb") as f:
            while True:
                piece = r.read(256 * 1024)
                if not piece:
                    break
                f.write(piece)
                size += len(piece)
    except (http.client.HTTPException, OSError) as e:
        server.close()
        raise Fail(2, "%s broke off after %d bytes: %s" % (rel, size, e))
    if r.will_close:
        server.close()
    expected = r.getheader("Content-Length")
    if expected and int(expected) != size:
        raise Fail(2, "%s broke off: %d of %s bytes" % (rel, size, expected))
    return size


def local_name(name):
    """A name from the phone as this computer can hold it (Windows forbids some characters)."""
    if os.name != "nt":
        return name
    return "".join("_" if c in '<>:"/\\|?*' or ord(c) < 32 else c for c in name).rstrip(". ") or "_"


def cmd_get(argv):
    pos, opt = parse(argv, flags=("recursive",))
    if not 1 <= len(pos) <= 2:
        raise Fail(1, "get <remote> [local] [-r]")
    server = need("files")
    rel = pos[0].strip("/")
    me, _ = propfind(server, rel, 0)
    name = local_name(me["name"] or "phone")
    local = pos[1] if len(pos) > 1 else name
    if not me["dir"]:
        if os.path.isdir(local):
            local = os.path.join(local, name)
        size = download(server, rel, local)
        return out("saved %s (%d bytes)" % (local, size))
    if "recursive" not in opt:
        raise Fail(1, "%s is a folder: add -r" % rel)
    count, total, unreadable = [0], [0], []
    os.makedirs(local, exist_ok=True)

    def visit(p, e):
        target = os.path.join(local, *[local_name(x) for x in p[len(rel):].strip("/").split("/")])
        if e["dir"]:
            os.makedirs(target, exist_ok=True)
        else:
            total[0] += download(server, p, target)
            count[0] += 1

    walk(server, rel, 64, visit, unreadable)
    out("saved %s (%d files, %d bytes)" % (local, count[0], total[0]))
    for u in unreadable:
        note("not readable: " + u)


def upload(server, local, rel):
    size = os.path.getsize(local)
    with open(local, "rb") as f:
        status, _, _ = server.request("PUT", server.path(rel), body=f, headers={"Content-Length": str(size)}, timeout=60)
    if status == 409:
        raise Fail(4, "%s: the folder to put it in is not there (409)" % rel)
    check(status, rel)
    return size


def is_folder(server, rel):
    try:
        return propfind(server, rel, 0)[0]["dir"]
    except Fail as e:
        if e.code == 4:
            return None
        raise


def make_folders(server, rel):
    made = ""
    for part in [p for p in rel.replace("\\", "/").split("/") if p]:
        made = join(made, part)
        status, _, _ = server.request("MKCOL", server.path(made))
        if status not in (201, 405):  # 405: it is there already
            check(status, made)


def cmd_put(argv):
    pos, opt = parse(argv, flags=("recursive",))
    if len(pos) != 2:
        raise Fail(1, "put <local> <remote> [-r]")
    local, rel = pos
    server = need("files")
    if not os.path.exists(local):
        raise Fail(1, "no such file here: " + local)
    into = rel.endswith("/") or rel.endswith("\\") or is_folder(server, rel)
    rel = rel.strip("/")
    if into:
        rel = join(rel, os.path.basename(os.path.abspath(local)))
    if os.path.isfile(local):
        size = upload(server, local, rel)
        return out("uploaded %s (%d bytes)" % (rel, size))
    if "recursive" not in opt:
        raise Fail(1, "%s is a folder: add -r" % local)
    count = total = 0
    make_folders(server, rel)
    for folder, dirs, files in os.walk(local):
        dirs.sort()
        sub = os.path.relpath(folder, local).replace(os.sep, "/")
        here = rel if sub == "." else join(rel, sub)
        if sub != ".":
            make_folders(server, here)
        for f in sorted(files):
            total += upload(server, os.path.join(folder, f), join(here, f))
            count += 1
    out("uploaded %s (%d files, %d bytes)" % (rel, count, total))


def cmd_mkdir(argv):
    pos, _ = parse(argv)
    if len(pos) != 1:
        raise Fail(1, "mkdir <remote>")
    make_folders(need("files"), pos[0])
    out("made " + pos[0].strip("/"))


def cmd_rm(argv):
    pos, opt = parse(argv, flags=("recursive",))
    if len(pos) != 1 or not pos[0].strip("/"):
        raise Fail(1, "rm <remote> [-r]")
    server = need("files")
    rel = pos[0].strip("/")
    folder = is_folder(server, rel)
    if folder is None:
        raise Fail(4, "%s: not found (404)" % rel)
    if folder and "recursive" not in opt:
        raise Fail(1, "%s is a folder: rm -r deletes it with everything inside, for good" % rel)
    status, _, _ = server.request("DELETE", server.path(rel))
    check(status, rel)
    out("deleted " + rel)


def move(argv, method, done):
    pos, opt = parse(argv, flags=("force",))
    if len(pos) != 2:
        raise Fail(1, "%s <from> <to> [--force]" % done)
    server = need("files")
    src, dst = pos[0].strip("/"), pos[1]
    folder = is_folder(server, src)
    if folder is None:
        raise Fail(4, "%s: not found (404)" % src)
    if dst.endswith("/") or is_folder(server, dst):
        dst = join(dst, src.split("/")[-1])
    dst = dst.strip("/")
    status, _, _ = server.request(method, server.path(src), headers={
        "Destination": server.path(dst), "Overwrite": "T" if "force" in opt else "F"})
    if status == 412:
        raise Fail(1, "%s is there already (--force replaces it)" % dst)
    if status == 409:
        raise Fail(4, "%s: the folder to put it in is not there (409)" % dst)
    check(status, src)
    out("%s %s -> %s" % ("moved" if method == "MOVE" else "copied", src, dst))


# --- the screen ---

class Stream:
    """The pictures of the screen as the broadcast sends them (multipart/x-mixed-replace)."""

    def __init__(self, server):
        self.server = server
        self.sock = None
        self.buffer = b""

    def open(self):
        s = self.server
        try:
            self.sock = socket.create_connection((s.host, s.port), timeout=s.timeout)
        except ConnectionRefusedError:
            raise Fail(2, "no answer from %s:%d: refused (the screen broadcast is not running)" % (s.host, s.port), refused=True)
        except (socket.timeout, TimeoutError):
            raise Fail(2, "no answer from %s:%d: %s" % (s.host, s.port, SILENT))
        except OSError as e:
            raise Fail(2, "no answer from %s:%d: %s" % (s.host, s.port, e))
        self.buffer = b""
        self.sock.sendall(("GET %sstream HTTP/1.1\r\nHost: %s:%d\r\nConnection: close\r\n\r\n" % (s.base, s.host, s.port)).encode("ascii"))
        head = self.until(b"\r\n\r\n", s.timeout)
        if head is None:
            raise Fail(2, "no answer from %s:%d: the stream did not start" % (s.host, s.port))
        status = head.split(b"\r\n", 1)[0].split(b" ")
        code = int(status[1]) if len(status) > 1 and status[1].isdigit() else 0
        if code != 200:
            self.close()
            check(code, "the screen address")

    def close(self):
        if self.sock:
            try:
                self.sock.close()
            except OSError:
                pass
        self.sock = None

    def fill(self, deadline):
        """More bytes into the buffer. False: the time is out. None: the phone ended the stream."""
        left = deadline - time.monotonic()
        if left <= 0 or not select.select([self.sock], [], [], left)[0]:
            return False
        try:
            piece = self.sock.recv(256 * 1024)
        except OSError:
            piece = b""
        if not piece:
            return None
        self.buffer += piece
        return True

    def until(self, mark, timeout):
        deadline = time.monotonic() + timeout
        while mark not in self.buffer:
            if not self.fill(deadline):
                return None
        i = self.buffer.index(mark)
        head, self.buffer = self.buffer[:i], self.buffer[i + len(mark):]
        return head

    def frame(self, timeout):
        """The next picture. False: none within the time. None: the phone ended the stream."""
        deadline = time.monotonic() + timeout
        while True:
            while self.buffer[:2] == b"\r\n":
                self.buffer = self.buffer[2:]
            if b"\r\n\r\n" in self.buffer:
                break
            got = self.fill(deadline)
            if not got:
                return got
        head, _, self.buffer = self.buffer.partition(b"\r\n\r\n")
        length = None
        for h in head.split(b"\r\n"):
            if h.lower().startswith(b"content-length:"):
                length = int(h.split(b":", 1)[1].strip())
        if length is None:
            return None
        while len(self.buffer) < length:
            got = self.fill(deadline + 5)  # a picture that began is read to its end
            if not got:
                return got
        picture, self.buffer = self.buffer[:length], self.buffer[length:]
        return picture


def jpeg_size(data):
    """Width and height from the JPEG's frame header."""
    i = 2
    while i + 9 < len(data):
        if data[i] != 0xFF:
            i += 1
            continue
        marker = data[i + 1]
        if marker in (0xD8, 0x01) or 0xD0 <= marker <= 0xD7:
            i += 2
            continue
        if marker == 0xFF:
            i += 1
            continue
        size = (data[i + 2] << 8) + data[i + 3]
        if 0xC0 <= marker <= 0xCF and marker not in (0xC4, 0xC8, 0xCC):
            return (data[i + 7] << 8) + data[i + 8], (data[i + 5] << 8) + data[i + 6]
        i += 2 + size
    return 0, 0


KEPT = 0.4   # the kept picture is sent at once; what comes later than this is live
PACE = 0.15  # the phone encodes a picture at most every 0.12 s: changes closer than that are not all seen


def observe(server, action=None, wait=12.0, react=2.5, settle=0.6, total=None, on_frame=None):
    """Watches the screen around an action and returns the last picture.

    The phone encodes pictures only while somebody watches and only when the screen changes, and a new
    watcher first gets the picture kept from the last time: it can be older than the screen. So the
    stream is opened first, the kept picture is taken, then the action runs, and the pictures that come
    after it are the truth. Returns (picture or None, how many came after the kept one, was one kept).

    The first live picture is waited for `react` s after an action, `settle` s when a kept one came,
    else `wait` s; after each one, `settle` s more; `total` s at most (the first wait + 5 by default).
    """
    stream = Stream(server)
    stream.open()
    try:
        last = stream.frame(KEPT) or None
        kept = last is not None
        if kept and on_frame:
            on_frame(last, True)
        if action:
            action()
        start = time.monotonic()
        first = react if action else (settle if kept else wait)
        end = start + (first + 5.0 if total is None else total)
        quiet = start + first
        new = 0
        while True:
            left = min(quiet, end) - time.monotonic()
            if left <= 0:
                break
            got = stream.frame(left)
            if got is False:
                break
            if got is None:
                # The phone ends the stream after 10 s without a change: watch again while there is time.
                # It then sends its kept picture once more; that is news only if it is another one.
                stream.close()
                stream.open()
                got = stream.frame(min(KEPT, max(0.05, min(quiet, end) - time.monotonic())))
                if not got or got == last:
                    continue
            last = got
            new += 1
            if on_frame:
                on_frame(got, False)
            quiet = time.monotonic() + settle
        return last, new, kept
    finally:
        stream.close()


NEVER = 2 ** 31 - 1  # no picture has this number: asking after it brings the number alone
POLL = 0.12          # how often the screen is asked while it is followed


def frame(server, after=None):
    """The screen as it is now, from a build that always keeps its newest frame (GET frame).

    None: the phone has no such address (an older build: see observe). Else (picture or None, number):
    no picture when nothing is newer than `after`, or when iOS has given the phone no frame yet (number 0).
    """
    status, r, data = server.request("GET", server.base + "frame" + ("" if after is None else "?after=%d" % after))
    if status == 404:
        return None
    if status == 503:
        return None, 0
    check(status, "the screen address", ok=(200, 204))
    text = r.getheader("X-Frame") or ""
    return (data if status == 200 else None), (int(text) if text.isdigit() else 0)


def follow(server, action=None, wait=0.0, react=2.5, settle=0.6, total=None, on_frame=None):
    """observe() for a build with GET frame: the phone knows its screen, so it is asked.

    Returns None, before the action is run, when the build has no GET frame. Else (picture or None, how
    many times the screen changed after the action, False). With on_frame the screen as it is at the
    start is reported too. The times are those of observe().
    """
    got = frame(server) if on_frame else frame(server, NEVER)
    if got is None:
        return None
    last, number = got if on_frame else (None, got[1])
    if on_frame and last is not None:
        on_frame(last, True)
    if action:
        action()
    start = time.monotonic()
    first = react if action else wait
    end = start + (first + 5.0 if total is None else total)
    quiet = start + first
    new = 0
    while True:
        left = min(quiet, end) - time.monotonic()
        if left <= 0:
            break
        time.sleep(min(POLL, left))
        got = frame(server, number)
        if got is None:
            break
        if got[0] is not None and got[1] > number:
            last, number, new = got[0], got[1], new + 1
            if on_frame:
                on_frame(last, False)
            quiet = time.monotonic() + settle
    if last is None:
        got = frame(server)
        last = got[0] if got else None
    return last, new, False


def shot_path(opt, pos=()):
    return (pos[0] if pos else None) or opt.get("out") or os.path.join(tempfile.gettempdir(), "iphone-shot.jpg")


def save_shot(picture, path):
    with open(path, "wb") as f:
        f.write(picture)
    w, h = jpeg_size(picture)
    s = load_state()
    s["shot"] = {"w": w, "h": h}
    save_state(s)
    return w, h


OWN_SETTLE = 0.8  # s after an action before LiteBro pictures its own screen


def app_shot(files):
    """LiteBro's own screen as a JPEG, made by the app itself (the phone's command shot): no broadcast is needed.
    None: this build of LiteBro has no such command."""
    status, _, data = files.request("POST", files.base + "_remote/shot", body=b"{}", headers={"Content-Type": "application/json"})
    if status == 403:
        raise Fail(3, "the phone refused (403): touch control is not switched on with the screen permission, or the "
                      "permission ran out")
    if status == 405 or (status == 404 and data.lstrip().startswith(b"{")):
        return None
    if status == 409:
        raise Fail(5, "LiteBro has no window to picture right now")
    check(status, "shot", ok=(200,))
    return data


NO_OWN = ("this build of LiteBro cannot picture its own screen: update it on the phone (iphone install), "
          "or use the screen broadcast (iphone connect <the screen address from the phone>)")


def own_screen(opt, pos=(), why=None):
    """shot --app: LiteBro's own screen, pictured by the app."""
    picture = app_shot(need("files"))
    if picture is None:
        raise Fail(1, NO_OWN)
    path = shot_path(opt, pos)
    w, h = save_shot(picture, path)
    out("shot %s %dx%d app" % (path, w, h))
    if why:
        note(why)


def cmd_shot(argv):
    pos, opt = parse(argv, flags=("app",), options=("wait", "out"))
    wait = number(opt, "wait", 12.0)
    state = load_state()
    if "app" in opt or (state.get("files") and not state.get("screen")):
        return own_screen(opt, pos)
    screen = need("screen")
    try:
        got = frame(screen)
    except Fail as e:
        # The phone is there and the broadcast is not: LiteBro's own screen, if this build can picture it
        if not (e.refused and state.get("files")):
            raise
        try:
            return own_screen(opt, pos, "note: the screen broadcast does not answer; this is LiteBro's own screen, pictured by the app.")
        except Fail:
            raise e
    if got is not None:
        deadline = time.monotonic() + wait
        while got[0] is None and time.monotonic() < deadline:
            time.sleep(0.3)  # the broadcast has only begun: iOS has given no frame yet
            got = frame(screen) or (None, 0)
        if got[0] is None:
            raise Fail(5, "no picture in %g s: the broadcast has only begun and iOS has given the phone no frame yet" % wait)
        path = shot_path(opt, pos)
        w, h = save_shot(got[0], path)
        return out("shot %s %dx%d live" % (path, w, h))
    picture, new, kept = observe(screen, wait=wait)
    if not picture:
        raise Fail(5, "no picture in %g s: the broadcast runs, but this build of LiteBro sends a picture only when the "
                      "screen changes while somebody watches. Do the next action with --shot, or have the screen touched." % wait)
    path = shot_path(opt, pos)
    w, h = save_shot(picture, path)
    out("shot %s %dx%d %s" % (path, w, h, "live" if new or not kept else "kept"))
    if kept and not new:
        note("note: this is the picture the phone kept from the last time somebody watched; the screen did not "
             "change while watching now. If it changed in between, the picture is older than the screen.")


def cmd_watch(argv):
    pos, opt = parse(argv, flags=("until-change",), options=("seconds",))
    seconds = number(opt, "seconds", 10.0)
    folder = pos[0] if pos else os.path.join(tempfile.gettempdir(), "iphone-watch")
    os.makedirs(folder, exist_ok=True)
    start = time.monotonic()
    count = [0]

    class Enough(Exception):
        pass

    first = ["now"]  # what the first picture is: the screen now, or (an older build) the one the phone kept

    def on_frame(picture, was_there):
        count[0] += 1
        path = os.path.join(folder, "frame-%03d.jpg" % count[0])
        w, h = save_shot(picture, path)
        out("%6.2f  %s  %dx%d%s" % (time.monotonic() - start, path, w, h, "  " + first[0] if was_there else ""))
        if "until-change" in opt and not was_there:
            raise Enough()

    try:
        screen = need("screen")
        if follow(screen, wait=seconds, settle=seconds, total=seconds, on_frame=on_frame) is None:
            first[0] = "kept"
            observe(screen, wait=seconds, settle=seconds, total=seconds, on_frame=on_frame)
    except Enough:
        return
    if "until-change" in opt:
        raise Fail(5, "the screen did not change in %g s" % seconds)
    if not count[0]:
        raise Fail(5, "no picture in %g s" % seconds)


# --- taps and text ---

TOUCH = ("system", "shot", "app")
TOUCH_OPTIONS = ("ms", "out", "of", "via")


def target(opt):
    """Where touch requests go: the app (it reaches the HID system and its own windows) or the broadcast extension."""
    via = opt.get("via", "app")
    if via not in ("app", "screen"):
        raise Fail(1, "--via app or --via screen")
    return (need("files"), "_touch") if via == "app" else (need("screen"), "touch")


def post_touch(server, name, payload):
    for key in ("index", "identity"):
        v = payload.get(key)
        if isinstance(v, (int, float)) and not isinstance(v, bool) and not 0 <= v <= 0xFFFFFFFF:
            raise Fail(1, "%s must be 0..4294967295: an older build of LiteBro crashes on anything else" % key)
    status, _, data = server.request("POST", server.base + name, body=json.dumps(payload).encode("utf-8"),
                                     headers={"Content-Type": "application/json"})
    if status == 403:
        raise Fail(3, "the phone refused the touch (403): touch control is not switched on with the screen "
                      "permission, or the permission ran out")
    if status == 200:
        # An in-app finger is answered with what it met. The report instead means the phone could not read
        # the request (text cut inside a character?) and did nothing.
        try:
            met = json.loads(data.decode("utf-8"))
        except ValueError:
            met = None
        if not isinstance(met, dict) or "available" in met:
            raise Fail(1, "the phone did not understand the touch request and did nothing")
        if met.get("error"):
            raise Fail(1, "the phone could not make the touch: %s" % met["error"])
        return met
    check(status, "touch", ok=(204,))
    return None


def coordinate(text, side, opt):
    try:
        v = float(text)
    except ValueError:
        raise Fail(1, "not a coordinate: " + text)
    if 0 <= v <= 1:
        return v
    size = None
    if "of" in opt:
        try:
            w, h = opt["of"].lower().split("x")
            size = {"w": float(w), "h": float(h)}
        except ValueError:
            raise Fail(1, "--of takes WIDTHxHEIGHT")
    else:
        size = load_state().get("shot")
    if not size or not size.get(side):
        raise Fail(1, "pixels need a picture to be of: take a shot first, or add --of WIDTHxHEIGHT (or give 0..1)")
    return min(max(v / float(size[side]), 0.0), 1.0)


def pairs(items, into=None):
    """key=value items as a request: true, false and numbers as such, the rest as text."""
    m = {} if into is None else into
    for pair in items:
        key, eq, value = pair.partition("=")
        if not eq or not key:
            raise Fail(1, "not key=value: " + pair)
        try:
            m[key] = json.loads(value)
        except ValueError:
            m[key] = value
    return m


def finger_options(opt):
    # Inside LiteBro: its own event queue. System-wide: made the way TrollVNC makes it (--opt recipe=none: the way
    # LiteBro made it before); a build that does not know the recipe ignores the key.
    return pairs(opt.get("opt", []), {"recipe": "trollvnc"} if "system" in opt else {"inApp": True})


def acted_own(opt, action, sent, before=None):
    """--shot without the screen broadcast: LiteBro pictures its own screen before and after the action."""
    files = need("files")
    if before is None:
        before = app_shot(files)
    if before is None:
        raise Fail(1, NO_OWN)
    action()
    time.sleep(OWN_SETTLE)
    sent()
    try:
        after = app_shot(files)
    except Fail as e:
        out("shot none changed=no")
        return note("note: no picture came after the action: %s" % e)
    if after is None:
        out("shot none changed=no")
        return note("note: no picture came after the action.")
    path = shot_path(opt)
    w, h = save_shot(after, path)
    out("shot %s %dx%d changed=%s app" % (path, w, h, "yes" if after != before else "no"))
    if after == before:
        note("note: LiteBro's own screen did not change within %g s after the action." % OWN_SETTLE)


def acted(opt, action, label, more=None, own=True):
    """Runs the action; with --shot watches the screen around it and saves what it became.
    `more` gives what to add to the line once the action is done. `own`: LiteBro's own picture will do
    when the screen broadcast is not there (not after a button: it takes LiteBro off the screen)."""
    sent = lambda: out(label + " sent" + (more() if more else ""))
    if "shot" not in opt:
        action()
        return sent()
    state = load_state()
    can_own = own and bool(state.get("files"))
    if "app" in opt or (can_own and not state.get("screen")):
        return acted_own(opt, action, sent)
    screen = need("screen")
    try:
        frame(screen, NEVER)  # asked before anything is sent: is the broadcast there?
    except Fail as e:
        if not (e.refused and can_own):
            raise
        try:
            before = app_shot(need("files"))
        except Fail:
            before = None
        if before is None:
            raise e
        note("note: the screen broadcast does not answer: looking at LiteBro's own screen instead.")
        return acted_own(opt, action, sent, before)
    result = follow(screen, action=action)
    asked = result is not None
    if not asked:
        result = observe(screen, action=action)  # an older build: its pictures have to be watched for
    picture, new, kept = result
    sent()
    if not picture:
        out("shot none changed=no")
        return note("note: no picture came: the screen did not change after the action (or the broadcast only began).")
    path = shot_path(opt)
    w, h = save_shot(picture, path)
    out("shot %s %dx%d changed=%s" % (path, w, h, "yes" if new else "no"))
    if not new:
        note("note: the screen did not change within 2.5 s after the action; the picture is "
             + ("the screen as it is." if asked else "the one kept from before."))


def cmd_tap(argv, hold=False):
    pos, opt = parse(argv, flags=TOUCH, options=TOUCH_OPTIONS, many=("opt",))
    if len(pos) != 2:
        raise Fail(1, "%s <x> <y>" % ("hold" if hold else "tap"))
    x, y = coordinate(pos[0], "w", opt), coordinate(pos[1], "h", opt)
    ms = number(opt, "ms", 800 if hold else 70)
    server, name = target(opt)
    extra = finger_options(opt)

    met = []

    def action():
        met.append(post_touch(server, name, dict(extra, t="down", x=x, y=y)))
        time.sleep(ms / 1000.0)
        post_touch(server, name, dict(extra, t="up", x=x, y=y))

    def more():
        # A build that answers an in-app finger says what was under it
        m = met[0] if met else None
        if not m:
            return ""
        if m.get("window") is False or m.get("enqueue") is False:
            return ", but LiteBro had no window to take it"
        return ", under the finger: %s" % (m.get("hit") or "nothing")

    acted(opt, action, "%s %.4f %.4f%s" % ("hold" if hold else "tap", x, y, " system" if "system" in opt else ""), more)


def cmd_swipe(argv):
    pos, opt = parse(argv, flags=TOUCH, options=TOUCH_OPTIONS + ("steps",), many=("opt",))
    if len(pos) != 4:
        raise Fail(1, "swipe <x1> <y1> <x2> <y2>")
    x1, y1 = coordinate(pos[0], "w", opt), coordinate(pos[1], "h", opt)
    x2, y2 = coordinate(pos[2], "w", opt), coordinate(pos[3], "h", opt)
    ms, steps = number(opt, "ms", 300), max(1, number(opt, "steps", 12, int))
    server, name = target(opt)
    extra = finger_options(opt)

    def action():
        post_touch(server, name, dict(extra, t="down", x=x1, y=y1))
        for i in range(1, steps + 1):
            time.sleep(ms / 1000.0 / steps)
            k = i / float(steps)
            last = i == steps  # the last step is the end point itself, not a rounding of it
            post_touch(server, name, dict(extra, t="move", x=x2 if last else x1 + (x2 - x1) * k,
                                          y=y2 if last else y1 + (y2 - y1) * k))
        post_touch(server, name, dict(extra, t="up", x=x2, y=y2))

    acted(opt, action, "swipe %.4f %.4f %.4f %.4f%s" % (x1, y1, x2, y2, " system" if "system" in opt else ""))


def cmd_type(argv):
    pos, opt = parse(argv, flags=("shot", "app", "enter"), options=("out", "file"))
    if "file" in opt and not pos:
        try:
            with open(opt["file"], "r", encoding="utf-8-sig", newline="") as f:
                text = f.read()
        except OSError:
            raise Fail(1, "no such file here: " + opt["file"])
    elif pos and "file" not in opt:
        text = " ".join(pos)
    else:
        raise Fail(1, "type <text> [--enter], or type --file <text file>")
    server = need("files")

    def action():
        post_touch(server, "_touch", {"type": text})
        if "enter" in opt:
            if "shot" in opt:
                time.sleep(PACE)
            post_touch(server, "_touch", {"type": "\n"})

    acted(opt, action, "type %d characters%s" % (len(text), " and enter" if "enter" in opt else ""))


def cmd_key(argv):
    pos, opt = parse(argv, flags=("shot", "app"), options=("count", "out"))
    keys = {"backspace": "\b", "enter": "\n"}
    if len(pos) != 1 or pos[0] not in keys:
        raise Fail(1, "key backspace|enter [--count N]")
    count = max(1, number(opt, "count", 1, int))
    server = need("files")

    def action():
        for i in range(count):
            if i and "shot" in opt:
                time.sleep(PACE)
            post_touch(server, "_touch", {"type": keys[pos[0]]})

    acted(opt, action, "key %s x%d" % (pos[0], count))


def cmd_button(argv):
    pos, opt = parse(argv, flags=("yes", "shot"), options=("via", "out"))
    if len(pos) != 1 or pos[0] not in ("home", "side"):
        raise Fail(1, "button home|side --yes")
    if "yes" not in opt:
        raise Fail(1, "button %s takes LiteBro off the screen: files, taps and text stop answering until the person "
                      "opens LiteBro again, and nothing from the computer can bring it back. Add --yes to send it." % pos[0])
    server, name = target(opt)
    acted(opt, lambda: post_touch(server, name, {"button": pos[0]}), "button %s" % pos[0], own=False)


def cmd_touch(argv):
    pos, opt = parse(argv, options=("via",))
    server, name = target(opt)
    if not pos:
        status, _, data = server.request("GET", server.base + name)
        check(status, "touch")
        return out(data.decode("utf-8", "replace"))
    if pos[0].lstrip().startswith("{"):
        try:
            payload = json.loads(" ".join(pos))
        except ValueError:
            raise Fail(1, "not JSON: " + " ".join(pos))
    else:
        payload = pairs(pos)
    post_touch(server, name, payload)
    out("sent")


# --- the page LiteBro shows: test commands of the phone (Remote.swift), no fingers involved ---

def address(url):
    """An address as iOS reads it: no space and no letter outside ASCII, the scheme in small letters."""
    scheme, sep, rest = url.strip().partition("://")
    if not sep:
        scheme, rest = "https", url.strip()
    return scheme.lower() + "://" + urllib.parse.quote(rest, safe="-._~:/?#[]@!$&'()*+,;=%")


def remote(name, payload=None, timeout=None):
    """POST _remote/<name> with JSON in and out. Returns (status, answer)."""
    files = need("files")
    status, _, data = files.request("POST", files.base + "_remote/" + urllib.parse.quote(name, safe=""),
                                    body=json.dumps(payload or {}).encode("utf-8"),
                                    headers={"Content-Type": "application/json"}, timeout=timeout)
    try:
        answer = json.loads(data.decode("utf-8")) if data else {}
    except (ValueError, RecursionError):
        if status == 200:
            raise Fail(1, "the phone's answer to %s is not JSON this helper can read (%d bytes)" % (name, len(data)))
        answer = {}
    if not isinstance(answer, dict):
        answer = {"result": answer}
    if status == 403:
        raise Fail(3, "the phone refused (403): touch control is not switched on with the screen permission, or the "
                      "permission ran out")
    if status == 405:
        raise Fail(1, "this build of LiteBro has no page commands: update it on the phone (iphone install)")
    if status == 404 and answer.get("error"):
        raise Fail(1, "the phone does not know the command %s" % name)
    if status == 400:
        raise Fail(1, "the phone did not take the request: wrong or missing %s" % answer.get("error", "value"))
    if status not in (200, 409, 504):
        check(status, name, ok=())
    return status, answer


def show_page(state, as_json=False):
    if as_json:
        return out(json.dumps(state))
    if state.get("error"):
        raise Fail(1, "LiteBro has no browser to ask: %s" % state["error"])
    out("url      " + (state.get("url") or "(the tiles: no page)"))
    out("title    " + (state.get("title") or ""))
    out("loading  " + ("yes" if state.get("loading") else "no"))
    out("tabs     %s" % state.get("tabs", "?"))


def cmd_page(argv):
    _, opt = parse(argv, flags=("json",))
    show_page(remote("state")[1], "json" in opt)


def cmd_open(argv):
    pos, opt = parse(argv, flags=("new-tab", "json"), options=("wait",))
    if len(pos) != 1:
        raise Fail(1, "open <address> [--new-tab] [--wait 20]")
    wait = number(opt, "wait", 20.0)
    _, state = remote("open", {"url": address(pos[0]), "newTab": "new-tab" in opt})
    deadline = time.monotonic() + wait
    while state.get("loading") and time.monotonic() < deadline:
        time.sleep(0.5)
        state = remote("state")[1]
    show_page(state, "json" in opt)
    if state.get("loading"):
        note("note: the page is still loading after %g s" % wait)


def cmd_js(argv):
    pos, opt = parse(argv, flags=("json",), options=("file", "timeout"))
    if "file" in opt and not pos:
        try:
            with open(opt["file"], "r", encoding="utf-8-sig") as f:
                code = f.read()
        except OSError:
            raise Fail(1, "no such file here: " + opt["file"])
    elif pos and "file" not in opt:
        code = " ".join(pos)
    else:
        raise Fail(1, "js <code>, or js --file <file>: the body of an async function, its return comes back")
    seconds = number(opt, "timeout", 20.0)
    status, answer = remote("js", {"code": code, "timeout": seconds}, timeout=seconds + 5)
    if status == 409:
        raise Fail(1, "no page is open in the current tab (it shows the tiles): open an address first")
    if status == 504:
        raise Fail(1, "the page's code did not finish in %g s" % seconds)
    if "error" in answer:
        raise Fail(1, "the page's code failed: %s" % answer["error"])
    result = answer.get("result")
    out(result if isinstance(result, str) and "json" not in opt else json.dumps(result))


def cmd_click(argv):
    pos, opt = parse(argv, flags=("shot", "app"), options=("out", "of"))
    if len(pos) != 2:
        raise Fail(1, "click <x> <y>")
    x, y = coordinate(pos[0], "w", opt), coordinate(pos[1], "h", opt)
    met = []

    def more():
        m = met[0] if met else {}
        return ", under the point: %s, control: %s" % (m.get("hit") or "nothing", m.get("control") or "none")

    acted(opt, lambda: met.append(remote("click", {"x": x, "y": y})[1]), "click %.4f %.4f" % (x, y), more)
    if met and not met[0].get("control"):
        note("note: there is no button or switch of LiteBro at that point, so nothing was pressed. click does not "
             "press what a page draws: use js for a page (document.querySelector(...).click()).")


def cmd_remote(argv):
    pos, _ = parse(argv)
    if not pos:
        raise Fail(1, "remote <name> [json | key=value ...]")
    if pos[0] == "shot":
        raise Fail(1, "shot gives a picture, not JSON: iphone shot --app")
    if len(pos) > 1 and pos[1].lstrip().startswith("{"):
        try:
            payload = json.loads(" ".join(pos[1:]))
        except ValueError:
            raise Fail(1, "not JSON: " + " ".join(pos[1:]))
    else:
        payload = pairs(pos[1:])
    status, answer = remote(pos[0], payload)
    out(json.dumps(answer))
    return 0 if status == 200 else 1


# --- the app ---

def read_log(server, size=256 * 1024):
    for rel in LOGS:
        status, _, data = server.request("GET", server.path(rel), headers={"Range": "bytes=-%d" % size})
        if status in (200, 206):
            return rel, data.decode("utf-8", "replace")
        if status != 404:
            check(status, rel)
    raise Fail(4, "LiteBro's log is not there (%s)" % " or ".join(LOGS))


def cmd_log(argv):
    pos, opt = parse(argv, flags=("launch",), options=("tail",))
    _, text = read_log(need("files"))
    lines = text.splitlines()
    if "launch" in opt:
        starts = [i for i, l in enumerate(lines) if l.startswith("===== launch")]
        if starts:
            first = starts[-1]
            # A launch that says the one before did not finish is shown with it: that one is where it went wrong
            if len(starts) > 1 and any(l.startswith("!! the last launch did not finish") for l in lines[first:]):
                first = starts[-2]
            lines = lines[first:]
        return out("\n".join(lines))
    out("\n".join(lines[max(0, len(lines) - number(opt, "tail", 60, int)):]))


def app_line(server):
    """`LiteBro 1.9.0 build 45363f3, iOS 16.2.0, iPhone14,5` of the last launch in the log."""
    try:
        _, text = read_log(server, 64 * 1024)
    except Fail:
        return None
    found = None
    for l in text.splitlines():
        if l.startswith("LiteBro ") and " build " in l:
            found = l
    return found


def probe_files(server):
    """What the files address answers: scope, writable, taps."""
    status, _, data = server.request("PROPFIND", server.base, headers={"Depth": "1"})
    if status == 404:
        return {"ok": False, "why": "wrong address: the phone answers 404 (an old secret? access was opened again)"}
    if status != 207:
        return {"ok": False, "why": "not the files address: it answers %d to a folder listing" % status}
    whole = b"Media" in data and b"Library" in data
    status, _, _ = server.request("PROPPATCH", server.base)
    writable = status == 207
    status, _, data = server.request("GET", server.base + "_touch")
    taps = status == 200
    return {"ok": True, "whole": whole, "writable": writable, "taps": taps,
            "hid": data.decode("utf-8", "replace") if taps else ""}


def probe_screen(server):
    status, _, data = server.request("GET", server.base)
    if status == 404:
        return {"ok": False, "why": "wrong address: the phone answers 404 (an old secret? the screen was allowed again)"}
    if status != 200 or b"stream" not in data:
        return {"ok": False, "why": "not the screen address"}
    return {"ok": True}


def kind_of(url):
    """Which of the two addresses this is, by asking it: 'files', 'screen' or the reason it is neither."""
    server = Server(url, timeout=5.0)
    try:
        status, r, data = server.request("OPTIONS", server.base)
    except Fail as e:
        # Nothing answers: the screen address is on 8081 and is silent until the broadcast runs
        return ("screen" if server.port == 8081 else "files"), str(e)
    finally:
        server.close()
    if r.getheader("DAV"):
        return "files", None
    if status == 200 and b"stream" in data:
        return "screen", None
    if status == 404:
        return ("screen" if server.port == 8081 else "files"), "wrong address: the phone answers 404 (an old secret?)"
    return None, "neither the files nor the screen address of LiteBro (it answers %d)" % status


def report(state, as_json=False):
    """The lines of `status`; returns the exit code."""
    info = {}
    code = 0
    lines = []
    if state.get("files"):
        server = Server(state["files"], timeout=5.0)
        try:
            p = probe_files(server)
        except Fail as e:
            p = {"ok": False, "why": str(e)}
            code = 2
        info["files"] = dict(p, url=server.url)
        if p["ok"]:
            lines.append("files   ok, %s, %s" % ("whole phone (/var/mobile)" if p["whole"] else "LiteBro folder only",
                                                 "writable" if p["writable"] else "read-only"))
            lines.append("taps    " + ("allowed" if p["taps"] else "off (touch control is not switched on, or the screen permission ran out)"))
            app = app_line(server)
            if app:
                info["app"] = app
                lines.append("app     " + app)
        else:
            lines.append("files   " + p["why"])
            code = code or 4
        server.close()
    else:
        lines.append("files   not set")
    if state.get("screen"):
        server = Server(state["screen"], timeout=5.0)
        try:
            p = probe_screen(server)
        except Fail as e:
            p = {"ok": False, "why": str(e)}
        info["screen"] = dict(p, url=server.url)
        lines.append("screen  " + ("ok, the broadcast runs" if p["ok"] else p["why"]))
        server.close()
    else:
        lines.append("screen  not set")
    if as_json:
        out(json.dumps(info))
    else:
        for l in lines:
            out(l)
    return code


def cmd_connect(argv):
    pos, opt = parse(argv, options=("files", "screen"))
    if len(pos) > 2 or not (pos or opt):
        raise Fail(1, "connect <address> [<address>], or connect --files <address> --screen <address>: "
                      "the addresses the phone shows")
    state = load_state()

    def full(url):
        return url if "://" in url else "http://" + url

    for kind in ("files", "screen"):
        if kind in opt:
            state[kind] = Server(full(opt[kind])).url
    for url in pos:
        kind, why = kind_of(full(url))
        if kind is None:
            raise Fail(1, "%s: %s" % (url, why))
        state[kind] = Server(full(url)).url
    state.pop("shot", None)
    save_state(state)
    code = report(state)
    out("saved   " + STATE)
    return code


def cmd_status(argv):
    _, opt = parse(argv, flags=("json",))
    state = load_state()
    if not state.get("files") and not state.get("screen"):
        raise Fail(1, "no address yet: iphone connect <address from the phone>")
    return report(state, "json" in opt)


def cmd_forget(argv):
    parse(argv)
    try:
        os.remove(STATE)
    except OSError:
        pass
    out("forgotten")


def cmd_wait(argv):
    pos, opt = parse(argv, options=("seconds",))
    kind = pos[0] if pos else "files"
    if kind not in ("files", "screen"):
        raise Fail(1, "wait [files|screen] [--seconds 120]")
    seconds = number(opt, "seconds", 120.0)
    deadline = time.monotonic() + seconds
    while True:
        server = need(kind)
        server.timeout = min(3.0, TIMEOUT)
        try:
            p = probe_files(server) if kind == "files" else probe_screen(server)
            if p["ok"]:
                return out("%s answers" % kind)
            why = p["why"]
        except Fail as e:
            why = str(e)
        finally:
            server.close()
        if time.monotonic() >= deadline:
            raise Fail(2, "%s did not answer in %g s: %s" % (kind, seconds, why))
        time.sleep(2)


def whole_phone(files):
    """Whether the build on the phone shares all of /var/mobile (LiteBro.tipa) and not LiteBro's folder alone."""
    names = [e["name"] for e in propfind(files, "")[1] if e["dir"]]
    return "Media" in names and "Containers" in names


def litebro_documents(files):
    """LiteBro's own folder in the Files app of the phone, as a path of the shared root."""
    if not whole_phone(files):
        return ""  # the build that shares only that folder: the root is it
    try:
        _, text = read_log(files, 64 * 1024)
    except Fail:
        text = ""
    home = None
    for l in text.splitlines():
        if l.startswith("data /var/mobile/"):
            home = l[len("data /var/mobile/"):].strip().strip("/")
    if not home:
        base = "Containers/Data/Application"
        for e in propfind(files, base)[1]:
            try:
                name = read_plist(files, join(base, e["name"]) + "/.com.apple.mobile_container_manager.metadata.plist").get("MCMMetadataIdentifier")
            except Fail:
                continue
            if name == "io.github.ronanime-arch.LiteBro":
                home = join(base, e["name"])
                break
    if not home:
        raise Fail(4, "LiteBro's own folder was not found on the phone")
    return home + "/Documents"


def cmd_install(argv):
    pos, opt = parse(argv, flags=("latest", "whole-phone"))
    if len(pos) > 1:
        raise Fail(1, "install (the released build), or install <file.tipa> (a file for TrollStore, opened by hand)")
    files = need("files")
    if not pos and "whole-phone" not in opt and not whole_phone(files):
        raise Fail(1, "this phone has the build that shares only LiteBro's folder (LiteBro-Browser.tipa), and install puts "
                      "the released LiteBro.tipa over it: the whole-phone build, which is not in Settings and cannot be the "
                      "default browser. Ask the person first, then add --whole-phone. To stay on the Browser build, pass "
                      "its file: iphone install LiteBro-Browser.tipa")
    if not pos:
        # The phone installs by itself only LiteBro's own released build, and only this address of it
        status, _, _ = files.request("POST", files.base + "_install", body=json.dumps({"url": OWN_BUILD}).encode("utf-8"),
                                     headers={"Content-Type": "application/json"})
        if status == 403:
            raise Fail(3, "the phone refused the install (403): it needs touch control switched on with the screen permission")
        if status == 409:
            raise Fail(3, "the phone did not start the install (409): its command would open the system Magnifier, not "
                          "TrollStore. Switch on URL Scheme in TrollStore's settings on the phone, then try again")
        check(status, "install", ok=(202,))
        out("asked the phone to install " + OWN_BUILD)
        return out("TrollStore opens on the phone and installs it (the person confirms there if it asks). LiteBro closes: "
                   "the person opens it again and opens access again, then connect with the new addresses.")
    source = pos[0]
    if not os.path.isfile(source):
        raise Fail(1, "no such file here: " + source)
    # Any other build goes to the phone as a file; the person opens it with TrollStore
    name = os.path.basename(os.path.abspath(source))
    rel = join(litebro_documents(files), name)
    size = upload(files, source, rel)
    out("uploaded %s (%d bytes)" % (rel, size))
    out("The phone installs by itself only the released build (iphone install). For this file the person opens the "
        "Files app on the phone, On My iPhone, LiteBro, %s, Share, TrollStore, Install." % name)


COMMANDS = {
    "connect": cmd_connect, "status": cmd_status, "forget": cmd_forget, "wait": cmd_wait,
    "ls": cmd_ls, "tree": cmd_tree, "find": cmd_find, "du": cmd_du, "cat": cmd_cat, "plist": cmd_plist, "apps": cmd_apps,
    "get": cmd_get, "put": cmd_put, "mkdir": cmd_mkdir, "rm": cmd_rm,
    "mv": lambda a: move(a, "MOVE", "mv"), "cp": lambda a: move(a, "COPY", "cp"),
    "shot": cmd_shot, "watch": cmd_watch,
    "page": cmd_page, "open": cmd_open, "js": cmd_js, "click": cmd_click, "remote": cmd_remote,
    "tap": cmd_tap, "hold": lambda a: cmd_tap(a, hold=True), "swipe": cmd_swipe, "type": cmd_type, "key": cmd_key,
    "button": cmd_button, "touch": cmd_touch,
    "log": cmd_log, "install": cmd_install,
}


def main(argv):
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass
    if not argv or argv[0] in ("help", "--help", "-h"):
        out(HELP)
        return 0
    command = COMMANDS.get(argv[0])
    if not command:
        note("iphone: unknown command %s (see: iphone help)" % argv[0])
        return 1
    try:
        return command(argv[1:]) or 0
    except Fail as e:
        note("iphone: " + str(e))
        return e.code
    except KeyboardInterrupt:
        return 130
    except OSError as e:
        note("iphone: %s" % e)
        return 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
