"""Browser history (Chrome, Edge, Brave, Vivaldi, Opera, Firefox), kept in memory and refreshed periodically.

Browsers lock their history databases while running, so each one is copied to a temp file first.
"""
import glob
import math
import os
import re
import shutil
import sqlite3
import tempfile
import threading
import time
from urllib.parse import urlparse

LOCAL = os.environ.get("LOCALAPPDATA", "")
ROAMING = os.environ.get("APPDATA", "")
CHROMIUM_ROOTS = {
    "Chrome": os.path.join(LOCAL, r"Google\Chrome\User Data"),
    "Edge": os.path.join(LOCAL, r"Microsoft\Edge\User Data"),
    "Brave": os.path.join(LOCAL, r"BraveSoftware\Brave-Browser\User Data"),
    "Vivaldi": os.path.join(LOCAL, r"Vivaldi\User Data"),
    "Opera": os.path.join(ROAMING, r"Opera Software\Opera Stable"),
}
CHROME_EPOCH_OFFSET = 11_644_473_600  # seconds between 1601-01-01 and 1970-01-01
PER_BROWSER_LIMIT = 20_000
REFRESH_SECONDS = 300


def _history_files():
    for browser, root in CHROMIUM_ROOTS.items():
        for path in glob.glob(os.path.join(root, "History")) + glob.glob(os.path.join(root, "*", "History")):
            yield browser, "chromium", path
    for path in glob.glob(os.path.join(ROAMING, r"Mozilla\Firefox\Profiles\*\places.sqlite")):
        yield "Firefox", "firefox", path


def _read(kind, path):
    tmp = os.path.join(tempfile.gettempdir(), f"winspotlight-hist-{os.getpid()}-{abs(hash(path))}.sqlite")
    shutil.copy2(path, tmp)
    try:
        con = sqlite3.connect(f"file:{tmp}?mode=ro", uri=True)
        if kind == "chromium":
            q = ("SELECT url, title, visit_count, last_visit_time / 1000000.0 - ? FROM urls "
                 "WHERE hidden = 0 ORDER BY last_visit_time DESC LIMIT ?")
            rows = con.execute(q, (CHROME_EPOCH_OFFSET, PER_BROWSER_LIMIT)).fetchall()
        else:
            q = ("SELECT url, title, visit_count, last_visit_date / 1000000.0 FROM moz_places "
                 "WHERE hidden = 0 AND last_visit_date IS NOT NULL ORDER BY last_visit_date DESC LIMIT ?")
            rows = con.execute(q, (PER_BROWSER_LIMIT,)).fetchall()
        con.close()
        return rows
    finally:
        try:
            os.remove(tmp)
        except OSError:
            pass


class BrowserHistory:
    def __init__(self):
        self.entries = []  # (haystack, url, title, visits, last_visit, browser)
        self._lock = threading.Lock()
        threading.Thread(target=self._loop, daemon=True).start()

    def _loop(self):
        while True:
            self.refresh()
            time.sleep(REFRESH_SECONDS)

    def refresh(self):
        best = {}
        for browser, kind, path in _history_files():
            try:
                rows = _read(kind, path)
            except (OSError, sqlite3.Error):
                continue
            for url, title, visits, last in rows:
                if not url.startswith(("http://", "https://")):
                    continue
                prev = best.get(url)
                if prev is None or last > prev[4]:
                    title = title or ""
                    best[url] = ((title + " " + url).lower(), url, title, visits or 0, last or 0, browser)
        with self._lock:
            self.entries = list(best.values())

    def search(self, query, limit=5):
        toks = re.findall(r"\w+", query.lower())
        if not toks:
            return []
        now = time.time()
        with self._lock:
            hits = [e for e in self.entries if all(t in e[0] for t in toks)]
        score = lambda e: math.log1p(e[3]) + 2 * math.exp(-(now - e[4]) / (86400 * 14))
        hits.sort(key=score, reverse=True)
        return [{"id": -1, "path": e[1], "name": e[2] or urlparse(e[1]).netloc, "kind": "web",
                 "size": e[3], "mtime": e[4], "snippet": f"{e[5]} · visited {e[3]}×"} for e in hits[:limit]]
