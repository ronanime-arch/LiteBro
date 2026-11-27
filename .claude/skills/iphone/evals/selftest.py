#!/usr/bin/env python3
"""Runs every command of the helper against the made-up phone (mock_phone.py).

    python selftest.py py            iphone.py with this Python
    python selftest.py powershell    iphone.ps1 in Windows PowerShell 5.1
    python selftest.py pwsh          iphone.ps1 in PowerShell 7
    python selftest.py py shot text  only the tests whose names contain these words

The made-up screen needs Pillow (pip install pillow). Both helpers must pass the same tests:
they are one tool in two languages.
"""
import contextlib
import json
import os
import plistlib
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import traceback

HERE = os.path.dirname(os.path.abspath(__file__))
SCRIPTS = os.path.join(os.path.dirname(HERE), "scripts")
sys.path.insert(0, HERE)
import mock_phone  # noqa: E402
from mock_phone import FakeApp, MockPhone, jpeg, sample_tree  # noqa: E402


def helper(kind):
    if kind == "py":
        return [sys.executable, os.path.join(SCRIPTS, "iphone.py")]
    if kind in ("powershell", "pwsh"):
        return [kind, "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", os.path.join(SCRIPTS, "iphone.ps1")]
    raise SystemExit(__doc__)


def free_port():
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
    s.close()
    return port


class Wrong(AssertionError):
    pass


class T:
    def __init__(self, kind):
        self.cmd = helper(kind)
        self.work = tempfile.mkdtemp(prefix="iphone-selftest-")
        self.state = os.path.join(self.work, "state.json")
        self.last = ""

    def run(self, *args, code=0, cwd=None, timeout=120):
        env = dict(os.environ, LITEBRO_IPHONE_STATE=self.state, LITEBRO_IPHONE_TIMEOUT="2", PYTHONIOENCODING="utf-8")
        p = subprocess.run(self.cmd + [str(a) for a in args], env=env, cwd=cwd or self.work, timeout=timeout,
                           stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        out = p.stdout.decode("utf-8", "replace").lstrip("﻿").replace("\r\n", "\n")
        err = p.stderr.decode("utf-8", "replace").lstrip("﻿").replace("\r\n", "\n")
        self.last = "$ iphone %s\nexit %d\n--- stdout\n%s--- stderr\n%s" % (" ".join(str(a) for a in args), p.returncode, out, err)
        if p.returncode != code:
            raise Wrong("exit code %d, expected %d\n%s" % (p.returncode, code, self.last))
        return out, err

    def has(self, text, part):
        if part not in text:
            raise Wrong("%r is not in the output\n%s" % (part, self.last))

    def lacks(self, text, part):
        if part in text:
            raise Wrong("%r is in the output\n%s" % (part, self.last))

    def eq(self, a, b, what=""):
        if a != b:
            raise Wrong("%s: %r != %r\n%s" % (what or "values differ", a, b, self.last))

    def ok(self, condition, what):
        if not condition:
            raise Wrong("%s\n%s" % (what, self.last))

    @contextlib.contextmanager
    def phone(self, whole=True, broadcast=True, connect=True, app=False, **kw):
        """A made-up phone, by default with the build that keeps its screen (GET frame); old_build=True: the one before."""
        root = tempfile.mkdtemp(prefix="phone-", dir=self.work)
        if whole:
            sample_tree(root)
        else:
            os.makedirs(os.path.join(root, "Downloads"))
            os.makedirs(os.path.join(root, "Debug"))
            with open(os.path.join(root, "Debug", "log.txt"), "w", encoding="utf-8") as f:
                f.write("===== launch =====\nLiteBro 1.9.0 build abc1234, iOS 16.2.0, iPhone14,5\n[0.1s 20 MB] first screen\n")
        ph = MockPhone(root, **kw)
        if app:
            FakeApp(ph)
        ph.start(broadcast=broadcast)
        try:
            if os.path.exists(self.state):
                os.remove(self.state)
            if connect:
                args = ["connect", "--files", ph.files_url]
                if broadcast:
                    args += ["--screen", ph.screen_url]
                self.run(*args)
            yield ph
        finally:
            ph.stop()

    def write(self, name, data):
        path = os.path.join(self.work, name)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as f:
            f.write(data if isinstance(data, bytes) else data.encode("utf-8"))
        return path


def read(path):
    with open(path, "rb") as f:
        return f.read()


# --- the tests ---

def test_help_and_misuse(t):
    out, _ = t.run("help")
    t.has(out, "connect")
    t.has(out, "install")
    if os.path.exists(t.state):
        os.remove(t.state)
    _, err = t.run("ls", code=1)
    t.has(err, "no files address yet")
    _, err = t.run("shot", code=1)
    t.has(err, "no screen address yet")
    _, err = t.run("dance", code=1)
    t.has(err, "unknown command")
    _, err = t.run("status", code=1)
    t.has(err, "no address yet")
    with t.phone():
        _, err = t.run("ls", "--loud", code=1)
        t.has(err, "unknown option")
        _, err = t.run("tree", "--depth", code=1)
        t.has(err, "needs a value")
        _, err = t.run("tree", "--depth", "deep", code=1)
        t.has(err, "takes a number")


def test_connect(t):
    with t.phone(connect=False) as ph:
        out, _ = t.run("connect", ph.screen_url, ph.files_url)  # in any order: each is asked what it is
        t.has(out, "files   ok, whole phone (/var/mobile), writable")
        t.has(out, "taps    allowed")
        t.has(out, "screen  ok")
        t.has(out, "app     LiteBro 1.9.0 build 45363f3, iOS 16.2.0, iPhone14,5")
        t.has(out, "saved   ")
        state = json.loads(read(t.state).decode("utf-8"))
        t.eq(state["files"], ph.files_url, "files address")
        t.eq(state["screen"], ph.screen_url, "screen address")
        # As people paste them: no slash at the end, no http://, the address of the stream itself
        t.run("forget")
        t.ok(not os.path.exists(t.state), "forget leaves the state file")
        t.run("connect", ph.files_url.rstrip("/")[len("http://"):], ph.screen_url + "stream")
        state = json.loads(read(t.state).decode("utf-8"))
        t.eq(state["files"], ph.files_url, "files address")
        t.eq(state["screen"], ph.screen_url, "screen address")
        out, _ = t.run("status", "--json")
        info = json.loads(out)
        t.eq(info["files"]["ok"], True)
        t.eq(info["files"]["taps"], True)
        t.eq(info["screen"]["ok"], True)
        t.eq(info["files"]["url"], ph.files_url)
        # Only one address: the other stays as it was
        t.run("connect", ph.files_url)
        t.eq(json.loads(read(t.state).decode("utf-8"))["screen"], ph.screen_url, "screen address kept")
        _, err = t.run("connect", "ftp://example.invalid/x/", code=1)
        t.has(err, "not an address of the phone")


def test_status(t):
    with t.phone(whole=False, control=False, read_only=True) as ph:
        out, _ = t.run("status")
        t.has(out, "files   ok, LiteBro folder only, read-only")
        t.has(out, "taps    off")
        t.has(out, "app     LiteBro 1.9.0 build abc1234")
    with t.phone(broadcast=False, connect=False) as ph:
        dead = "http://127.0.0.1:%d/abcdef/" % free_port()
        out, _ = t.run("connect", "--files", ph.files_url, "--screen", dead)
        t.has(out, "files   ok")
        t.has(out, "screen  no answer")
        t.has(out, "refused")
        # LiteBro left the screen: iOS froze it. The port is open and nothing answers.
        ph.suspend()
        out, _ = t.run("status", code=2)
        t.has(out, "files   no answer")
        t.has(out, "timeout (LiteBro is not on the screen")
        _, err = t.run("ls", code=2)
        t.has(err, "timeout (LiteBro is not on the screen")
        # The person came back to LiteBro: the same address answers again
        ph.resume()
        out, _ = t.run("status")
        t.has(out, "files   ok")
        # Access closed, or its time over: nothing listens
        ph.close_access()
        out, _ = t.run("status", code=2)
        t.has(out, "refused (nothing listens there")
        _, err = t.run("ls", code=2)
        t.has(err, "refused")
    with t.phone(connect=False) as ph:
        out, _ = t.run("connect", "--files", ph.files_url.replace(ph.files_token, "oldsecret"), code=4)
        t.has(out, "wrong address")
        _, err = t.run("ls", code=4)
        t.has(err, "404")


def test_listing(t):
    with t.phone() as ph:
        out, _ = t.run("ls")
        t.eq([l.split()[-1] for l in out.splitlines()], ["Containers/", "Library/", "Media/"], "the root")
        out, _ = t.run("ls", "Media/DCIM/100APPLE")
        lines = out.splitlines()
        t.eq(len(lines), 3)
        t.ok(lines[0].startswith("f ") and lines[0].endswith("IMG_0001.JPG"), "a file line")
        size = os.path.getsize(os.path.join(ph.root, "Media", "DCIM", "100APPLE", "IMG_0003.MOV"))
        t.eq(lines[2].split()[1], str(size), "the size")
        t.ok(lines[2].split()[2].endswith("Z") and "T" in lines[2].split()[2], "the time is UTC, ISO")
        out, _ = t.run("ls", "/Media/Downloads/")
        t.has(out, "Отчёт за ноябрь.pdf")  # as typed: iOS gives the name with ё and й taken apart
        os.makedirs(os.path.join(ph.root, "Media", "Downloads", "Новый файл"))
        out, _ = t.run("ls", "Media/Downloads", "--json")
        t.eq([i["name"] for i in json.loads(out)], ["Новый файл", "Отчёт за ноябрь.pdf"], "names in the form people type")
        out, _ = t.run("find", "Media", "*отчёт*")
        t.eq(out.splitlines(), ["Media/Downloads/Отчёт за ноябрь.pdf\t17"])
        out, _ = t.run("find", "Media", "новый*")
        t.eq(out.splitlines(), ["Media/Downloads/Новый файл/"])
        os.rmdir(os.path.join(ph.root, "Media", "Downloads", "Новый файл"))
        out, _ = t.run("ls", "Media/Downloads/Отчёт за ноябрь.pdf")  # a file: its own line
        t.eq(len(out.splitlines()), 1)
        out, _ = t.run("ls", "Media", "--json")
        items = json.loads(out)
        t.eq([(i["name"], i["dir"]) for i in items], [("DCIM", True), ("Downloads", True), ("LiteBro-Debug", True)])
        os.makedirs(os.path.join(ph.root, "Media", "Empty"))
        out, _ = t.run("ls", "Media/Empty")
        t.has(out, "(empty)")
        _, err = t.run("ls", "Media/Nothing", code=4)
        t.has(err, "404")

        out, _ = t.run("tree", "Media", "--depth", "3")
        t.has(out, "DCIM/\n  100APPLE/\n    IMG_0001.JPG  (")
        out, _ = t.run("tree", "Media", "--depth", "1")
        t.lacks(out, "100APPLE")
        out, _ = t.run("tree", "Media", "--depth", "5", "--max", "2")
        t.has(out, "stopped at 2 entries")

        out, _ = t.run("find", "Media", "*.jpg")  # the case does not matter
        t.eq(sorted(l.split("\t")[0] for l in out.splitlines()), ["Media/DCIM/100APPLE/IMG_0001.JPG", "Media/DCIM/100APPLE/IMG_0002.JPG"])
        out, _ = t.run("find", "", "log.txt", "--depth", "2")
        t.has(out, "(nothing)")
        out, _ = t.run("find", "", "100*")
        t.has(out, "Media/DCIM/100APPLE/")

        out, _ = t.run("du", "Media")
        lines = out.splitlines()
        total = sum(os.path.getsize(os.path.join(d, f)) for d, _, fs in os.walk(os.path.join(ph.root, "Media")) for f in fs)
        t.eq(lines[-1].split()[0], str(total), "the total")
        t.ok(lines[-1].endswith("total"), "the total line")
        t.ok(lines[0].endswith("DCIM/"), "the biggest first")


def test_reading(t):
    with t.phone() as ph:
        out, _ = t.run("cat", "Media/LiteBro-Debug/log.txt")
        t.has(out, "===== launch")
        t.has(out, "launch finished")
        out, _ = t.run("cat", "Media/LiteBro-Debug/log.txt", "--tail", "2")
        t.eq(out.splitlines(), ["[0.44s 55 MB] first screen", "[5.45s 61 MB] launch finished"])
        out, _ = t.run("cat", "Media/LiteBro-Debug/log.txt", "--head", "2")
        t.eq(out.splitlines()[1], "===== launch 2026-11-26 15:40:02 +0000 =====")
        _, err = t.run("cat", "Media/DCIM/100APPLE/IMG_0003.MOV", code=1)
        t.has(err, "is not text")
        _, err = t.run("cat", "Media", code=1)
        t.has(err, "is a folder")
        # Russian text comes out as it is
        with open(os.path.join(ph.root, "Media", "note.txt"), "w", encoding="utf-8") as f:
            f.write("Привет, телефон\nвторая строка\n")
        out, _ = t.run("cat", "Media/note.txt")
        t.eq(out.splitlines(), ["Привет, телефон", "вторая строка"])

        app = "Containers/Data/Application/0B9D7C2E-AAAA-4BBB-8CCC-DDDDEEEEFFFF"
        out, _ = t.run("plist", app + "/Library/Preferences/io.github.ronanime-arch.LiteBro.plist")
        prefs = json.loads(out)
        t.eq(prefs["tabs"], ["https://claude.ai/", "", "http://192.168.1.10:3000/"])
        t.eq(prefs["currentTab"], 2)
        t.eq(prefs["adBlock"], True)
        # Every kind of value a property list holds, binary and XML
        import datetime
        value = {"text": "Привет 👋", "ascii": "plain", "int": -5, "big": 2 ** 40, "real": 1.5, "yes": True, "no": False,
                 "when": datetime.datetime(2026, 11, 26, 15, 40, 2), "bytes": b"\x00\x01\xff",
                 "list": [1, "two", [3]], "dict": {"inner": {"deep": "x"}}, "many": list(range(300)), "long": "я" * 40}
        for fmt, name in ((plistlib.FMT_BINARY, "b.plist"), (plistlib.FMT_XML, "x.plist")):
            with open(os.path.join(ph.root, "Media", name), "wb") as f:
                plistlib.dump(value, f, fmt=fmt)
            out, _ = t.run("plist", "Media/" + name)
            got = json.loads(out)
            t.eq(got["text"], "Привет 👋", name)
            t.eq(got["int"], -5, name)
            t.eq(got["big"], 2 ** 40, name)
            t.eq(got["real"], 1.5, name)
            t.eq((got["yes"], got["no"]), (True, False), name)
            t.eq(got["when"], "2026-11-26T15:40:02Z", name)
            t.eq(got["bytes"], {"data": "AAH/"}, name)
            t.eq(got["list"], [1, "two", [3]], name)
            t.eq(got["dict"], {"inner": {"deep": "x"}}, name)
            t.eq(got["many"], list(range(300)), name)
            t.eq(got["long"], "я" * 40, name)
        _, err = t.run("plist", "Media/note.txt", code=1)
        t.has(err, "is not a property list")

        out, _ = t.run("apps")
        t.eq(out.splitlines(), ["com.apple.mobilenotes\tContainers/Data/Application/7A1E55D0-1234-4ABC-9DEF-0123456789AB",
                                "io.github.ronanime-arch.LiteBro\t" + app])
        out, _ = t.run("apps", "litebro")
        t.eq(len(out.splitlines()), 1)
        out, _ = t.run("apps", "telegram")
        t.has(out, "(nothing)")

        out, _ = t.run("log", "--tail", "3")
        t.eq(out.splitlines()[-1], "[5.45s 61 MB] launch finished")
        t.eq(len(out.splitlines()), 3)
        out, _ = t.run("log", "--launch")
        t.ok(out.startswith("===== launch"), "the last launch from its first line")
    with t.phone(whole=False):
        out, _ = t.run("log")
        t.has(out, "build abc1234")
        _, err = t.run("apps", code=4)
        t.has(err, "LiteBro's own folder")


def test_get(t):
    with t.phone() as ph:
        src = os.path.join(ph.root, "Media", "DCIM", "100APPLE")
        out, _ = t.run("get", "Media/DCIM/100APPLE/IMG_0003.MOV", os.path.join(t.work, "movie.mov"))
        t.has(out, "saved ")
        t.eq(read(os.path.join(t.work, "movie.mov")), read(os.path.join(src, "IMG_0003.MOV")), "the file")
        # Without a name: into the current folder under its own name; into a folder: the same
        t.run("get", "Media/Downloads/Отчёт за ноябрь.pdf")
        t.eq(read(os.path.join(t.work, "Отчёт за ноябрь.pdf")), b"%PDF-1.4 made up\n")
        os.makedirs(os.path.join(t.work, "into"))
        t.run("get", "Media/DCIM/100APPLE/IMG_0001.JPG", os.path.join(t.work, "into"))
        t.eq(read(os.path.join(t.work, "into", "IMG_0001.JPG")), read(os.path.join(src, "IMG_0001.JPG")))
        # A big one, whole
        big = os.urandom(5 * 1024 * 1024 + 123)
        with open(os.path.join(ph.root, "Media", "big.bin"), "wb") as f:
            f.write(big)
        t.run("get", "Media/big.bin", os.path.join(t.work, "big.bin"))
        t.eq(read(os.path.join(t.work, "big.bin")) == big, True, "5 MB arrive whole")
        with open(os.path.join(ph.root, "Media", "empty.txt"), "wb"):
            pass
        t.run("get", "Media/empty.txt", os.path.join(t.work, "empty.txt"))
        t.eq(read(os.path.join(t.work, "empty.txt")), b"")

        _, err = t.run("get", "Media/DCIM", os.path.join(t.work, "dcim"), code=1)
        t.has(err, "add -r")
        out, _ = t.run("get", "Media/DCIM", os.path.join(t.work, "dcim"), "-r")
        t.has(out, "(3 files, ")
        for name in ("IMG_0001.JPG", "IMG_0002.JPG", "IMG_0003.MOV"):
            t.eq(read(os.path.join(t.work, "dcim", "100APPLE", name)), read(os.path.join(src, name)), name)
        _, err = t.run("get", "Media/nothing.txt", code=4)
        t.has(err, "404")
        # A folder with a Russian name comes under the name as typed, whatever form the phone keeps it in
        out, _ = t.run("get", "Media/Downloads", os.path.join(t.work, "dl"), "-r")
        t.ok(os.path.exists(os.path.join(t.work, "dl", "Отчёт за ноябрь.pdf")), "the local name is the typed one: %r" % os.listdir(os.path.join(t.work, "dl")))
        if os.name != "nt":
            # What a link leads to, not the link: a linked folder is a folder, a linked file comes whole
            os.symlink(os.path.join(ph.root, "Media", "DCIM"), os.path.join(ph.root, "Media", "Camera"))
            os.symlink(os.path.join(src, "IMG_0003.MOV"), os.path.join(ph.root, "Media", "movie-link.mov"))
            out, _ = t.run("ls", "Media")
            t.has(out, "Camera/")
            t.run("get", "Media/movie-link.mov", os.path.join(t.work, "linked.mov"))
            t.eq(read(os.path.join(t.work, "linked.mov")) == read(os.path.join(src, "IMG_0003.MOV")), True, "a linked file whole")
            out, _ = t.run("ls", "Media/Camera/100APPLE")
            t.eq(len(out.splitlines()), 3)


def test_put(t):
    with t.phone() as ph:
        local = t.write("up/привет мир.txt", "раз\nдва\n")
        out, _ = t.run("put", local, "Media/Downloads/привет мир.txt")
        t.has(out, "uploaded Media/Downloads/привет мир.txt (14 bytes)")
        t.eq(read(os.path.join(ph.root, "Media", "Downloads", "привет мир.txt")), "раз\nдва\n".encode("utf-8"))
        # Into a folder: under its own name, with or without the slash
        t.run("put", local, "Media/DCIM")
        t.ok(os.path.exists(os.path.join(ph.root, "Media", "DCIM", "привет мир.txt")), "put into a folder")
        t.run("put", local, "Media/DCIM/100APPLE/")
        t.ok(os.path.exists(os.path.join(ph.root, "Media", "DCIM", "100APPLE", "привет мир.txt")), "put into a folder/")
        # Over an existing file
        local2 = t.write("up/second.txt", "new")
        t.run("put", local2, "Media/Downloads/привет мир.txt")
        t.eq(read(os.path.join(ph.root, "Media", "Downloads", "привет мир.txt")), b"new")
        # Names that need care in an address and in a shell
        for name in ("a #1 100% [x] (y) & z+w = q, 'p'; @!$~.txt", "снимок 📱.bin"):
            data = os.urandom(2000)
            t.run("put", t.write("odd/" + name, data), "Media/Downloads/" + name)
            t.eq(read(os.path.join(ph.root, "Media", "Downloads", name)), data, name)
            t.run("get", "Media/Downloads/" + name, os.path.join(t.work, "back.bin"))
            t.eq(read(os.path.join(t.work, "back.bin")), data, name + " back")
            out, _ = t.run("ls", "Media/Downloads")
            t.has(out, name)
        big = os.urandom(6 * 1024 * 1024 + 7)
        t.run("put", t.write("up/big.bin", big), "Media/big.bin")
        t.eq(read(os.path.join(ph.root, "Media", "big.bin")) == big, True, "6 MB arrive whole")
        t.run("put", t.write("up/empty.bin", b""), "Media/empty.bin")
        t.eq(read(os.path.join(ph.root, "Media", "empty.bin")), b"")
        t.eq([n for n in os.listdir(os.path.join(ph.root, "Media")) if n.startswith(".litebro-upload-")], [], "no upload leftovers")

        t.write("tree/a.txt", "a")
        t.write("tree/sub/b.txt", "bb")
        t.write("tree/sub/deep/c с пробелом.txt", "ccc")
        os.makedirs(os.path.join(t.work, "tree", "hollow"))
        _, err = t.run("put", os.path.join(t.work, "tree"), "Media/copy", code=1)
        t.has(err, "add -r")
        out, _ = t.run("put", os.path.join(t.work, "tree"), "Media/copy", "-r")
        t.has(out, "uploaded Media/copy (3 files, 6 bytes)")
        t.eq(read(os.path.join(ph.root, "Media", "copy", "sub", "deep", "c с пробелом.txt")), b"ccc")
        t.ok(os.path.isdir(os.path.join(ph.root, "Media", "copy", "hollow")), "an empty folder is made too")

        _, err = t.run("put", local, "Media/NoSuchFolder/x.txt", code=4)
        t.has(err, "is not there")
        t.eq([n for n in os.listdir(os.path.join(ph.root, "Media")) if n.startswith(".litebro-upload-")], [], "no upload leftovers")
        _, err = t.run("put", os.path.join(t.work, "no-such-file"), "Media/x.txt", code=1)
        t.has(err, "no such file here")


def test_changes(t):
    with t.phone() as ph:
        media = os.path.join(ph.root, "Media")
        out, _ = t.run("mkdir", "Media/New/Deeper/Папка")
        t.has(out, "made Media/New/Deeper/Папка")
        t.ok(os.path.isdir(os.path.join(media, "New", "Deeper", "Папка")), "nested folders")
        t.run("mkdir", "Media/New")  # there already: fine

        out, _ = t.run("mv", "Media/DCIM/100APPLE/IMG_0001.JPG", "Media/New/first.jpg")
        t.has(out, "moved Media/DCIM/100APPLE/IMG_0001.JPG -> Media/New/first.jpg")
        t.ok(os.path.exists(os.path.join(media, "New", "first.jpg")), "moved")
        t.ok(not os.path.exists(os.path.join(media, "DCIM", "100APPLE", "IMG_0001.JPG")), "gone from the old place")
        t.run("mv", "Media/New/first.jpg", "Media/New/Deeper")  # into a folder
        t.ok(os.path.exists(os.path.join(media, "New", "Deeper", "first.jpg")), "moved into a folder")
        out, _ = t.run("cp", "Media/New/Deeper/first.jpg", "Media/New/Deeper/Папка/копия.jpg")
        t.has(out, "copied ")
        t.ok(os.path.exists(os.path.join(media, "New", "Deeper", "first.jpg")), "the original stays")
        t.eq(read(os.path.join(media, "New", "Deeper", "Папка", "копия.jpg")), read(os.path.join(media, "New", "Deeper", "first.jpg")))
        # Not over another file unless told
        _, err = t.run("mv", "Media/DCIM/100APPLE/IMG_0002.JPG", "Media/New/Deeper/first.jpg", code=1)
        t.has(err, "is there already")
        t.ok(os.path.exists(os.path.join(media, "DCIM", "100APPLE", "IMG_0002.JPG")), "nothing moved")
        second = read(os.path.join(media, "DCIM", "100APPLE", "IMG_0002.JPG"))
        t.run("mv", "Media/DCIM/100APPLE/IMG_0002.JPG", "Media/New/Deeper/first.jpg", "--force")
        t.eq(read(os.path.join(media, "New", "Deeper", "first.jpg")), second)
        t.run("mv", "Media/New/Deeper", "Media/Moved")  # a folder
        t.ok(os.path.isdir(os.path.join(media, "Moved", "Папка")), "a folder moves with what is inside")
        _, err = t.run("mv", "Media/nothing", "Media/x", code=4)
        t.has(err, "404")
        _, err = t.run("mv", "Media/Moved/first.jpg", "Media/NoSuchFolder/x.jpg", code=4)
        t.has(err, "is not there")

        _, err = t.run("rm", "Media/Moved", code=1)
        t.has(err, "rm -r")
        t.ok(os.path.isdir(os.path.join(media, "Moved")), "a folder is not deleted without -r")
        out, _ = t.run("rm", "Media/Moved/first.jpg")
        t.has(out, "deleted Media/Moved/first.jpg")
        t.ok(not os.path.exists(os.path.join(media, "Moved", "first.jpg")), "deleted")
        t.run("rm", "Media/Moved", "-r")
        t.ok(not os.path.exists(os.path.join(media, "Moved")), "the folder is deleted with -r")
        _, err = t.run("rm", "Media/Moved", code=4)
        t.has(err, "404")
        _, err = t.run("rm", "/", code=1)
    with t.phone(read_only=True) as ph:
        local = t.write("ro.txt", "x")
        for args in (("put", local, "Media/ro.txt"), ("mkdir", "Media/RO"), ("rm", "Media/Downloads/Отчёт за ноябрь.pdf"),
                     ("mv", "Media/Downloads/Отчёт за ноябрь.pdf", "Media/x.pdf")):
            _, err = t.run(*args, code=3)
            t.has(err, "refused (403)")
        t.ok(os.path.exists(os.path.join(ph.root, "Media", "Downloads", "Отчёт за ноябрь.pdf")), "read-only keeps the file")


A = jpeg(["picture A"], size=(400, 800))
B = jpeg(["picture B"], size=(300, 650), background=(90, 20, 20))
C = jpeg(["picture C"], size=(300, 650), background=(20, 90, 20))


def later(seconds, action):
    timer = threading.Timer(seconds, action)
    timer.daemon = True
    timer.start()


def test_shot(t):
    # The build that keeps its screen: one question, the screen as it is
    with t.phone() as ph:
        frames = ph.frames
        started = time.time()
        _, err = t.run("shot", os.path.join(t.work, "s.jpg"), "--wait", "2", code=5)
        t.has(err, "no picture in 2 s: the broadcast has only begun")
        t.ok(time.time() - started >= 2, "it waited for the first frame")
        frames.screen_changed(A)  # nobody watches: the phone keeps it all the same
        out, err = t.run("shot", os.path.join(t.work, "s.jpg"))
        t.has(out, "s.jpg 400x800 live")
        t.eq(read(os.path.join(t.work, "s.jpg")), A)
        t.lacks(err, "note:")
        frames.screen_changed(B)
        frames.screen_changed(C)  # at once after B: the older build would have lost it
        out, _ = t.run("shot")
        path = out.strip().rsplit(" ", 2)[0][len("shot "):]
        t.ok(os.path.basename(path) == "iphone-shot.jpg" and os.path.exists(path), "the default file: " + path)
        t.eq(read(path), C, "the newest frame, although it came at once after another")
        t.run("shot", "--out", os.path.join(t.work, "o.jpg"))
        t.eq(read(os.path.join(t.work, "o.jpg")), C)
        ph.stop_broadcast()
        _, err = t.run("shot", code=2)
        t.has(err, "refused")
        ph.start_broadcast()  # a new broadcast: the extension starts again, with the frames of its own start
        out, _ = t.run("shot", os.path.join(t.work, "s.jpg"))
        t.eq(read(os.path.join(t.work, "s.jpg")), C)
    # The build before: pictures only while somebody watches the stream
    with t.phone(idle=2.0, old_build=True) as ph:
        frames = ph.frames
        # A broadcast that has just begun and a still screen: the phone has nothing to send
        started = time.time()
        _, err = t.run("shot", os.path.join(t.work, "s.jpg"), "--wait", "3", code=5)
        t.has(err, "no picture in 3 s")
        t.ok(time.time() - started >= 3, "it waited (and watched again after the phone ended the stream)")
        # The screen changes while it waits: that picture is live
        frames.on_join = lambda: (setattr(frames, "on_join", None), later(1.0, lambda: frames.screen_changed(A)))
        out, err = t.run("shot", os.path.join(t.work, "s.jpg"), "--wait", "6")
        t.has(out, "s.jpg 400x800 live")
        t.eq(read(os.path.join(t.work, "s.jpg")), A, "the live picture")
        t.lacks(err, "note:")
        # Nobody watched while the screen changed: the phone still holds A, and says nothing about B
        frames.truth = B
        out, err = t.run("shot", os.path.join(t.work, "s.jpg"))
        t.has(out, "400x800 kept")
        t.has(err, "note: this is the picture the phone kept")
        t.eq(read(os.path.join(t.work, "s.jpg")), A, "the kept picture")
        # It changes again while watching: the kept one first, then the news
        frames.on_join = lambda: (setattr(frames, "on_join", None), later(0.3, lambda: frames.screen_changed(B)),
                                  later(0.6, lambda: frames.screen_changed(C)))
        out, err = t.run("shot", os.path.join(t.work, "s.jpg"))
        t.has(out, "300x650 live")
        t.eq(read(os.path.join(t.work, "s.jpg")), C, "the last of the new pictures")
        ph.stop_broadcast()
        _, err = t.run("shot", code=2)
        t.has(err, "refused")
        ph.start_broadcast()  # a new run of the extension has no picture
        _, err = t.run("shot", "--wait", "1", code=5)
    with t.phone() as ph:
        state = json.loads(read(t.state).decode("utf-8"))
        state["screen"] = state["screen"].replace(ph.screen_token, "oldsecret")
        with open(t.state, "w", encoding="utf-8") as f:
            json.dump(state, f)
        _, err = t.run("shot", code=4)
        t.has(err, "404")


def test_watch(t):
    folder = os.path.join(t.work, "watch")
    with t.phone() as ph:
        frames = ph.frames
        frames.screen_changed(A)
        later(0.6, lambda: frames.screen_changed(B))
        later(1.2, lambda: frames.screen_changed(C))
        time.sleep(0.01)
        out, _ = t.run("watch", folder, "--seconds", "2.5")
        lines = out.splitlines()
        if len(lines) == 3:  # the helper began before the first change
            t.ok(lines[0].endswith("now"), "the first is the screen now")
            t.eq(read(os.path.join(folder, "frame-001.jpg")), A)
            t.eq(read(os.path.join(folder, "frame-003.jpg")), C)
        t.ok(len(lines) in (2, 3), "the pictures as the screen changed")
        t.eq(read(os.path.join(folder, "frame-%03d.jpg" % len(lines))), C, "the last picture is the last change")
        _, err = t.run("watch", folder, "--seconds", "1", "--until-change", code=5)
        t.has(err, "did not change")
    with t.phone(idle=2.0, old_build=True) as ph:
        frames = ph.frames
        frames.frame, frames.number = A, 1
        frames.on_join = lambda: (setattr(frames, "on_join", None), later(0.5, lambda: frames.screen_changed(B)),
                                  later(1.0, lambda: frames.screen_changed(C)))
        out, _ = t.run("watch", folder, "--seconds", "2.5")
        lines = out.splitlines()
        t.eq(len(lines), 3, "three pictures")
        t.ok(lines[0].endswith("kept"), "the first is the kept one")
        t.eq(read(os.path.join(folder, "frame-001.jpg")), A)
        t.eq(read(os.path.join(folder, "frame-002.jpg")), B)
        t.eq(read(os.path.join(folder, "frame-003.jpg")), C)
        # Until the screen changes
        frames.on_join = lambda: (setattr(frames, "on_join", None), later(0.7, lambda: frames.screen_changed(A)))
        started = time.time()
        out, _ = t.run("watch", folder, "--seconds", "20", "--until-change")
        t.ok(time.time() - started < 15, "it stops at the first change")
        t.eq(read(os.path.join(folder, "frame-002.jpg")), A)
        _, err = t.run("watch", folder, "--seconds", "1.5", "--until-change", code=5)
        t.has(err, "did not change")


def fingers(ph):
    return [e for e in ph.take_events() if e["kind"] == "finger"]


def test_touch(t):
    with t.phone() as ph:
        out, _ = t.run("tap", "0.25", "0.75")
        t.has(out, "tap 0.2500 0.7500 sent, under the finger: UIView")
        e = fingers(ph)
        t.eq([(x["t"], x["x"], x["y"], x["inApp"], x["via"]) for x in e],
             [("down", 0.25, 0.75, True, "app"), ("up", 0.25, 0.75, True, "app")], "a tap inside the app")
        t.ok(0.03 <= e[1]["at"] - e[0]["at"] < 1.0, "the finger stays down for a moment")
        # System-wide, with the options of the request
        out, _ = t.run("tap", "0.5", "0.5", "--system", "--opt", "client=simple", "--opt", "bks=false", "--opt", "index=3",
                       "--opt", "pressure=0.5", "--opt", "sender=0x8000000817319371")
        t.has(out, "system")
        e = fingers(ph)
        t.eq(e[0]["inApp"], False)
        t.eq(e[0]["opts"], {"recipe": "trollvnc", "client": "simple", "bks": False, "index": 3, "pressure": 0.5,
                            "sender": "0x8000000817319371"}, "a system-wide finger names the recipe, and the options change it")
        t.run("tap", "0.5", "0.5", "--system", "--opt", "recipe=none", "--opt", "masks=webkit", "--opt", "radius=5", "--opt", "twist=90")
        t.eq(fingers(ph)[0]["opts"], {"recipe": "none", "masks": "webkit", "radius": 5, "twist": 90})
        t.run("tap", "0.5", "0.5", "--opt", "recipe=trollvnc")
        t.eq(fingers(ph)[0]["opts"], {"inApp": True, "recipe": "trollvnc"}, "the recipe for an in-app finger too")
        # Pixels of a picture
        t.run("tap", "250", "1500", "--of", "1000x2000")
        e = fingers(ph)
        t.eq((e[0]["x"], e[0]["y"]), (0.25, 0.75), "pixels of --of")
        ph.frames.frame, ph.frames.number = A, 1  # 400x800
        t.run("shot", os.path.join(t.work, "p.jpg"))
        t.run("tap", "100", "600")
        e = fingers(ph)
        t.eq((e[0]["x"], e[0]["y"]), (0.25, 0.75), "pixels of the last shot")
        t.run("tap", "1", "1")  # the far corner as a fraction
        t.eq(fingers(ph)[0]["x"], 1.0)
        _, err = t.run("tap", "left", "0.5", code=1)
        t.has(err, "not a coordinate")
        _, err = t.run("tap", "0.5", "0.5", "--system", "--opt", "index=-1", code=1)
        t.has(err, "index must be 0..4294967295")
        _, err = t.run("touch", "t=down", "x=0.5", "y=0.5", "identity=5000000000", code=1)
        t.has(err, "identity must be")
        t.eq(fingers(ph), [], "nothing was sent")
        _, err = t.run("tap", "0.5", code=1)

        t.run("hold", "0.1", "0.2", "--ms", "400")
        e = fingers(ph)
        t.eq([x["t"] for x in e], ["down", "up"])
        t.ok(e[1]["at"] - e[0]["at"] >= 0.35, "a long press holds")

        out, _ = t.run("swipe", "0.5", "0.8", "0.5", "0.2", "--steps", "5", "--ms", "200")
        t.has(out, "swipe 0.5000 0.8000 0.5000 0.2000 sent")
        e = fingers(ph)
        t.eq([x["t"] for x in e], ["down"] + ["move"] * 5 + ["up"], "a swipe")
        ys = [x["y"] for x in e]
        t.ok(all(a >= b - 1e-9 for a, b in zip(ys, ys[1:])) and abs(ys[0] - 0.8) < 1e-6 and abs(ys[-1] - 0.2) < 1e-6, "it moves evenly: %r" % ys)
        t.eq((e[-2]["x"], e[-2]["y"]), (0.5, 0.2), "the last step is the end point")
        t.ok(abs(ys[3] - 0.44) < 1e-6, "the third step is at 0.44: %r" % ys)
        t.ok(e[-1]["at"] - e[0]["at"] >= 0.15, "a swipe takes its time")

        # Through the broadcast extension instead of the app
        t.run("tap", "0.5", "0.5", "--via", "screen", "--system")
        t.eq([x["via"] for x in fingers(ph)], ["screen", "screen"])

        out, _ = t.run("touch")
        t.eq(json.loads(out)["available"], True, "what the phone can do")
        t.run("touch", '{"t":"down","x":0.4,"y":0.6,"client":"admin","parentXY":false}')
        e = fingers(ph)
        t.eq((e[0]["x"], e[0]["y"], e[0]["opts"]), (0.4, 0.6, {"client": "admin", "parentXY": False}), "a raw request")
        t.run("touch", "t=up", "x=0.4", "y=0.6", "inApp=true")
        e = fingers(ph)
        t.eq((e[0]["t"], e[0]["inApp"]), ("up", True), "a raw request as key=value")
        _, err = t.run("touch", "{nonsense", code=1)
    with t.phone(control=False) as ph:
        _, err = t.run("tap", "0.5", "0.5", code=3)
        t.has(err, "refused the touch (403)")
        _, err = t.run("type", "x", code=3)
        _, err = t.run("touch", code=3)
        t.eq(ph.take_events(), [], "nothing reached the phone")
    with t.phone(broadcast=False) as ph:
        # Looking needs the screen address: nothing is sent without it
        _, err = t.run("tap", "0.5", "0.5", "--shot", code=1)
        t.has(err, "no screen address yet")
        t.eq(ph.take_events(), [])


def test_look(t):
    for old in (False, True):
        with t.phone(app=True, old_build=old) as ph:
            app = ph.on_event.__self__

            def seen(shot, what):
                # The build that keeps its screen shows the screen at rest. The one before shows the last picture
                # it made, and that can be a frame from the middle of the movement.
                t.eq(read(shot), ph.frames.frame, what + ": the picture the phone made last")
                if not old:
                    t.eq(read(shot), ph.frames.truth, what + ": the screen at rest")

            # The made-up LiteBro: a tap on the address field brings the keyboard
            shot = os.path.join(t.work, "look.jpg")
            out, err = t.run("tap", "0.3", "0.93", "--shot", "--out", shot)
            t.has(out, "tap 0.3000 0.9300 sent" + ("" if old else ", under the finger: UITextField"))
            t.has(out, "look.jpg 591x1280 changed=yes")
            t.eq(app.focus, True, "the field took the tap")
            seen(shot, "after the tap")
            out, _ = t.run("type", "example.com", "--shot", "--out", shot)
            t.has(out, "type 11 characters sent")
            t.has(out, "changed=yes")
            seen(shot, "after the text")
            out, _ = t.run("key", "backspace", "--count", "4", "--shot", "--out", shot)
            t.has(out, "key backspace x4 sent")
            t.eq(app.field, "example")
            seen(shot, "after the backspaces")
            out, _ = t.run("type", ".org", "--enter", "--shot", "--out", shot)
            t.has(out, "type 4 characters and enter sent")
            t.eq(app.url, "https://example.org", "the made-up browser went there")
            seen(shot, "after enter")
            # A tap by the pixels of that picture
            t.run("tap", "150", "1190", "--shot", "--out", shot)
            t.eq(app.focus, True, "the field took the tap by pixels")
            # A system-wide finger does nothing, as on the phone
            time.sleep(0.3)
            out, err = t.run("tap", "0.5", "0.3", "--system", "--shot", "--out", shot)
            t.has(out, "changed=no")
            t.has(err, "did not change")
            t.has(err, "the one kept from before" if old else "the screen as it is")
            seen(shot, "after nothing")
            out, _ = t.run("swipe", "0.5", "0.8", "0.5", "0.3", "--shot", "--out", shot)
            t.has(out, "changed=no")
    with t.phone(app=True) as ph:
        # A broadcast that has only begun: the first look is already the screen
        out, _ = t.run("shot", os.path.join(t.work, "first.jpg"))
        t.has(out, "591x1280 live")
        t.eq(read(os.path.join(t.work, "first.jpg")), ph.frames.truth)
    with t.phone(app=True, old_build=True) as ph:
        # The older build has nothing yet: the first look is the action itself
        out, _ = t.run("tap", "0.3", "0.93", "--shot", "--out", os.path.join(t.work, "first.jpg"))
        t.has(out, "changed=yes")
        t.eq(read(os.path.join(t.work, "first.jpg")), ph.frames.frame)
    for old in (False, True):
        with t.phone(old_build=old) as ph:
            out, err = t.run("tap", "0.5", "0.5", "--shot")
            t.has(out, "shot none changed=no")
            t.has(err, "no picture came")


def test_page(t):
    with t.phone(app=True) as ph:
        app = ph.on_event.__self__
        out, _ = t.run("page")
        t.eq(out.splitlines(), ["url      (the tiles: no page)", "title    ", "loading  no", "tabs     1"])
        _, err = t.run("js", "return document.title", code=1)
        t.has(err, "no page is open")
        # An address as people write it
        out, _ = t.run("open", "example.org")
        t.eq(out.splitlines()[:2], ["url      https://example.org", "title    The page of example.org"])
        t.eq(app.url, "https://example.org")
        t.eq([(e["kind"], e["url"], e["newTab"]) for e in ph.take_events()], [("open", "https://example.org", False)])
        t.run("open", "HTTP://пример.рф/путь страницы?q=1", "--new-tab")
        e = ph.take_events()
        t.eq((e[0]["url"], e[0]["newTab"]), ("http://%D0%BF%D1%80%D0%B8%D0%BC%D0%B5%D1%80.%D1%80%D1%84/%D0%BF%D1%83%D1%82%D1%8C%20%D1%81%D1%82%D1%80%D0%B0%D0%BD%D0%B8%D1%86%D1%8B?q=1", True))
        t.run("open", "https://example.org/")
        out, _ = t.run("page", "--json")
        t.eq(json.loads(out)["url"], "https://example.org/")
        # JavaScript of the page
        ph.take_events()
        out, _ = t.run("js", "return document.title")
        t.eq(out, "The page of example.org\n")
        t.eq([(e["kind"], e["code"]) for e in ph.take_events()], [("js", "return document.title")])
        out, _ = t.run("js", "return", "42")
        t.eq(out, "42\n")
        out, _ = t.run("js", "return document.title", "--json")
        t.eq(json.loads(out), "The page of example.org")
        out, _ = t.run("js", "document.querySelector('a').click()")
        t.eq(out, "null\n")
        code = 'const s = "кавычки \'и\' такие";\nreturn document.body.innerText'
        out, _ = t.run("js", "--file", t.write("page.js", code))
        t.eq(out.splitlines(), ["The page of example.org", "loaded."])
        t.eq(ph.take_events()[-1]["code"], code)
        _, err = t.run("js", "throw new Error(1)", code=1)
        t.has(err, "the page's code failed")
        _, err = t.run("js", code=1)
        # A button of LiteBro itself, without a finger
        out, _ = t.run("tap", "0.3", "0.93")
        t.run("type", "claude.ai")
        ph.take_events()
        out, err = t.run("click", "0.87", "0.93")
        t.has(out, "click 0.8700 0.9300 sent, under the point: UIButton, control: UIButton")
        t.eq(app.url, "https://claude.ai", "the Go button got its action")
        t.eq([(e["kind"], e["control"]) for e in ph.take_events()], [("click", "UIButton")])
        out, err = t.run("click", "0.5", "0.3")
        t.has(out, "control: none")
        t.has(err, "nothing was pressed")
        out, _ = t.run("click", "0.87", "0.93", "--shot", "--out", os.path.join(t.work, "click.jpg"))
        t.has(out, "click 0.8700 0.9300 sent")
        t.has(out, "changed=")
        # Any other command of the phone, as it is
        out, _ = t.run("remote", "state")
        t.eq(json.loads(out)["page"], True)
        out, _ = t.run("remote", "open", "url=https://example.com/", "newTab=true")
        t.eq(json.loads(out)["url"], "https://example.com/")
        out, _ = t.run("remote", "js", '{"code": "return location.href"}')
        t.eq(json.loads(out), {"result": "https://example.com/"})
        _, err = t.run("remote", "dance", code=1)
        t.has(err, "does not know the command dance")
        _, err = t.run("open", code=1)
    with t.phone(app=True, control=False):
        for args in (("page",), ("open", "example.org"), ("js", "return 1"), ("click", "0.5", "0.5")):
            _, err = t.run(*args, code=3)
            t.has(err, "refused (403)")
    with t.phone(app=True, old_build=True):
        _, err = t.run("page", code=1)
        t.has(err, "has no page commands")
    with t.phone(app=True) as ph:
        # Fingers that do nothing, as on the user's phone on 2026-11-27: the page commands still do the work
        ph.dead_taps = True
        out, _ = t.run("tap", "0.3", "0.93", "--shot", "--out", os.path.join(t.work, "dead.jpg"))
        t.has(out, "changed=no")
        t.run("open", "example.org")
        t.eq(ph.on_event.__self__.url, "https://example.org")


def test_text(t):
    with t.phone() as ph:
        text = "Привет, мир! 👋 q&a <1> 100% it's"
        out, _ = t.run("type", text)
        t.has(out, "sent")
        t.eq([e["text"] for e in ph.take_events()], [text], "the text as typed")
        t.run("type", "two", "words")
        t.eq([e["text"] for e in ph.take_events()], ["two words"])
        t.run("type", "go", "--enter")
        t.eq([e["text"] for e in ph.take_events()], ["go", "\n"])
        # From a file: any text, whatever the shell would do to it
        hard = 'кавычки "двойные" и \'одинарные\', $HOME `x` %PATH% \\ конец\nвторая строка'
        t.run("type", "--file", t.write("text.txt", hard))
        t.eq([e["text"] for e in ph.take_events()], [hard])
        t.run("key", "backspace", "--count", "3")
        t.eq([e["text"] for e in ph.take_events()], ["\b", "\b", "\b"])
        t.run("key", "enter")
        t.eq([e["text"] for e in ph.take_events()], ["\n"])
        _, err = t.run("key", "escape", code=1)
        _, err = t.run("type", code=1)


def test_button(t):
    with t.phone(app=True) as ph:
        ph.leaves_after = None
        _, err = t.run("button", "home", code=1)
        t.has(err, "takes LiteBro off the screen")
        t.eq(ph.take_events(), [], "nothing is sent without --yes")
        _, err = t.run("button", "volume", "--yes", code=1)
        out, _ = t.run("button", "side", "--yes")
        t.has(out, "button side sent")
        t.eq([(e["kind"], e["button"]) for e in ph.take_events()], [("button", "side")])
        # Home, and a look at what the screen became: the screen server goes on while LiteBro is away
        ph.leaves_after = 0.5
        shot = os.path.join(t.work, "home.jpg")
        out, _ = t.run("button", "home", "--yes", "--shot", "--out", shot)
        t.has(out, "button home sent")
        t.has(out, "changed=yes")
        t.eq([(e["kind"], e["button"]) for e in ph.take_events()], [("button", "home")])
        t.eq(ph.on_event.__self__.screen, "springboard")
        t.eq(read(shot), ph.frames.truth, "the Home screen of iOS")
        # LiteBro is frozen now: its address takes connections and says nothing
        _, err = t.run("tap", "0.5", "0.5", code=2)
        t.has(err, "timeout (LiteBro is not on the screen")
        out, _ = t.run("shot", shot)  # the screen is still shown
        t.has(out, "live")


def test_install(t):
    own = mock_phone.OWN_BUILD
    with t.phone() as ph:
        # The phone installs by itself only LiteBro's own released build
        out, _ = t.run("install")
        t.has(out, "asked the phone to install " + own)
        t.has(out, "TrollStore opens on the phone")
        t.eq([x["url"] for x in ph.take_events() if x["kind"] == "install"], [own])
        time.sleep(0.9)  # TrollStore put the build in LiteBro's place: the old addresses are gone
        _, err = t.run("ls", code=2)
        t.has(err, "refused")
    with t.phone() as ph:
        ph.installs = False
        t.run("install", "--latest")
        t.eq([x["url"] for x in ph.take_events()], [own])
        # Any other build goes to the phone as a file, into LiteBro's own folder of the Files app
        build = os.urandom(300 * 1024 + 11)
        local = t.write("builds/LiteBro PR.tipa", build)
        out, _ = t.run("install", local)
        app = "Containers/Data/Application/0B9D7C2E-AAAA-4BBB-8CCC-DDDDEEEEFFFF"
        t.has(out, "uploaded %s/Documents/LiteBro PR.tipa (%d bytes)" % (app, len(build)))
        t.has(out, "TrollStore, Install")
        t.eq(read(os.path.join(ph.root, *app.split("/"), "Documents", "LiteBro PR.tipa")) == build, True, "the build is in LiteBro's folder")
        t.eq(ph.take_events(), [], "the phone was not asked to install it")
        # The log does not say where LiteBro lives: its folder is found by its name
        os.remove(os.path.join(ph.root, "Media", "LiteBro-Debug", "log.txt"))
        t.run("install", local)
        _, err = t.run("install", os.path.join(t.work, "none.tipa"), code=1)
        t.has(err, "no such file here")
        _, err = t.run("install", local, local, code=1)
        # TrollStore's URL scheme is off: the command would open the system Magnifier, and the phone says so
        ph.magnifier = True
        _, err = t.run("install", code=3)
        t.has(err, "Magnifier")
        t.has(err, "URL Scheme")
        t.eq(ph.take_events(), [])
    with t.phone(whole=False) as ph:
        # The build that shares only LiteBro's folder: the file goes to its top
        local = t.write("builds/y.tipa", b"y" * 10)
        out, _ = t.run("install", local)
        t.has(out, "uploaded y.tipa (10 bytes)")
        t.eq(read(os.path.join(ph.root, "y.tipa")), b"y" * 10)
    with t.phone(control=False) as ph:
        _, err = t.run("install", code=3)
        t.has(err, "refused the install (403)")


def test_wait(t):
    with t.phone() as ph:
        out, _ = t.run("wait", "--seconds", "5")
        t.has(out, "files answers")
        out, _ = t.run("wait", "screen", "--seconds", "5")
        t.has(out, "screen answers")
        ph.suspend()
        _, err = t.run("wait", "files", "--seconds", "1", code=2)
        t.has(err, "did not answer in 1 s")
        later(1.5, ph.resume)
        started = time.time()
        out, _ = t.run("wait", "files", "--seconds", "30")
        t.has(out, "files answers")
        t.ok(time.time() - started < 20, "it returns as soon as the phone is back")


TESTS = [test_help_and_misuse, test_connect, test_status, test_listing, test_reading, test_get, test_put, test_changes,
         test_shot, test_watch, test_touch, test_look, test_page, test_text, test_button, test_install, test_wait]


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    t = T(sys.argv[1])
    wanted = sys.argv[2:]
    failed = []
    try:
        for test in TESTS:
            name = test.__name__[5:]
            if wanted and not any(w in name for w in wanted):
                continue
            started = time.time()
            try:
                test(t)
                print("ok    %-18s %5.1f s" % (name, time.time() - started), flush=True)
            except Exception as e:
                failed.append(name)
                print("FAIL  %-18s %5.1f s" % (name, time.time() - started), flush=True)
                if isinstance(e, Wrong):
                    frame = traceback.extract_tb(e.__traceback__)[1]
                    print("  line %d: %s\n  %s" % (frame.lineno, frame.line, str(e).replace("\n", "\n  ")), flush=True)
                else:
                    print("  " + traceback.format_exc().replace("\n", "\n  ") + "\n  " + t.last.replace("\n", "\n  "), flush=True)
    finally:
        shutil.rmtree(t.work, ignore_errors=True)
    if failed:
        print("\n%d failed: %s" % (len(failed), ", ".join(failed)))
        return 1
    print("\nall passed (%s)" % sys.argv[1])
    return 0


if __name__ == "__main__":
    sys.exit(main())
