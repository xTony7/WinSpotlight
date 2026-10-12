"""Headless backend for the WinUI front end: indexer + search + LLM over localhost HTTP.

    GET  /status                -> {"status": str, "hotkey": str, "llm": str|null}
    GET  /search?q=...&rerank=0|1          -> {"top": {...}|null, "sections": [{"title", "items"}], "calc": str|null}
    POST /task  {"q": str}      -> event stream of agent steps (see agent.py)
    POST /ask   {"q": str}      -> text/event-stream of  data: {"t": "..."}  /  data: {"error": "..."}
    POST /open  {"path", "reveal"}
    POST /reindex
    POST /shutdown
"""
import argparse
import re
import json
import os
import subprocess
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlparse

from . import config, store
from .embeddings import Embedder
from .agent import run_task
from .history import BrowserHistory
from .indexer import Indexer
from .llm import build_messages, make_provider
from .llamaserver import LlamaServer
from .search import SearchEngine

FIELDS = ("id", "path", "name", "kind", "size", "mtime", "snippet")


class Backend:
    def __init__(self):
        self.cfg = config.load()
        self.embedder = Embedder(self.cfg["embedding"])
        self.vindex = store.VectorIndex()
        con = store.connect(config.DB_PATH)
        store.ensure_model(con, f"{self.cfg['embedding']['backend']}:{self.cfg['embedding']['model']}")
        self.vindex.load(con)
        self.engine = SearchEngine(con, self.embedder, self.vindex)
        self.lock = threading.Lock()  # one sqlite connection shared by request threads
        self.generation = 0
        self.llm = make_provider(self.cfg["llm"])
        self.model_server = LlamaServer(self.cfg["llm"]) if self.llm else None
        self.indexer = Indexer(self.cfg, self.embedder, self.vindex)
        self.indexer.start()
        self.history = BrowserHistory() if self.cfg["browser_history"] else None

    def search(self, q, rerank=True):
        # Each keystroke sends a search; drop ones superseded while waiting for the lock
        # so slow natural-language queries don't pile up behind each other.
        self.generation += 1
        gen = self.generation
        self.embedder.last_search = time.time()
        with self.lock:
            if gen != self.generation:
                return {"top": None, "calc": None, "sections": [], "task": None, "stale": True}
            top, sections, calc = self.engine.search(q, rerank)
        pick = lambda r: {k: r.get(k) for k in FIELDS}
        out = [{"title": t, "items": [pick(r) for r in items]} for t, items in sections]
        web = self.history.search(q) if self.history else []
        if web:  # always the last section, below local results
            out.append({"title": "Browser History", "items": web})
        task = None
        if self.llm:
            target = self.resolve_target(q)
            task = {"target": target} if target else None
        return {"top": pick(top) if top else None, "calc": calc, "sections": out, "task": task}

    PATHISH = re.compile(r'"([^"]+)"|(\S+[\\/]\S+|\S+\.[A-Za-z0-9]{1,6})')

    def resolve_target(self, q):
        """Find the file a task refers to: a quoted/absolute path, or an indexed file name."""
        # Unquoted absolute paths may contain spaces ("C:\Users\Jane Doe\a.py it crashes"):
        # from each drive letter, take the longest run of words that names an existing file.
        for m in re.finditer(r"(?<![\w])[A-Za-z]:[\\/]", q):
            words = q[m.start():].split(" ")
            for n in range(len(words), 0, -1):
                cand = " ".join(words[:n]).strip("\"'.,;:")
                if os.path.isfile(cand):
                    return os.path.abspath(cand)
        for m in self.PATHISH.finditer(q):
            tok = (m.group(1) or m.group(2)).strip(".,;:'")
            if os.path.isfile(tok):
                return os.path.abspath(tok)
            name = os.path.basename(tok.replace("/", "\\"))
            with self.lock:
                rows = self.engine.con.execute(
                    "SELECT path FROM files WHERE name = ? COLLATE NOCASE AND kind != 'folder' "
                    "ORDER BY mtime DESC LIMIT 20", (name,)).fetchall()
            tail = tok.replace("/", "\\").lower()
            for r in rows:  # prefer matches whose path ends with what the user typed, e.g. src/app.py
                if r[0].lower().endswith(tail):
                    return r[0]
            if rows:
                return rows[0][0]
        return None

    def context(self, q):
        if not self.cfg["llm"]["use_search_context"]:
            return ""
        with self.lock:
            top, sections, _ = self.engine.search(q)
            results = ([top] if top else []) + [r for _, items in sections for r in items]
            return self.engine.context_for(results[:5])


class Handler(BaseHTTPRequestHandler):
    backend: Backend = None

    def log_message(self, *_):
        pass

    def _json(self, obj, code=200):
        body = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _body(self):
        if "chunked" in (self.headers.get("Transfer-Encoding") or "").lower():
            data = b""
            while (size := int(self.rfile.readline().strip() or b"0", 16)):
                data += self.rfile.read(size)
                self.rfile.readline()
            self.rfile.readline()
            return json.loads(data or b"{}")
        n = int(self.headers.get("Content-Length") or 0)
        return json.loads(self.rfile.read(n) or b"{}")

    def do_GET(self):
        b, url = self.backend, urlparse(self.path)
        if url.path == "/status":
            self._json({"status": b.indexer.last_status, "hotkey": b.cfg["hotkey"],
                        "llm": b.llm.name if b.llm else None})
        elif url.path == "/search":
            qs = parse_qs(url.query)
            self._json(b.search(qs.get("q", [""])[0], qs.get("rerank", ["1"])[0] != "0"))
        else:
            self._json({"error": "not found"}, 404)

    def do_POST(self):
        b, path = self.backend, urlparse(self.path).path
        if path == "/open":
            d = self._body()
            try:
                if d.get("reveal") and not d["path"].startswith(("http://", "https://")):
                    subprocess.Popen(["explorer", "/select,", d["path"]])
                else:
                    os.startfile(d["path"])
                self._json({"ok": True})
            except OSError as e:
                self._json({"ok": False, "error": str(e)})
        elif path == "/shutdown":  # the front end restarts us after config changes
            self._json({"ok": True})
            threading.Timer(0.2, lambda: os._exit(0)).start()
        elif path == "/reindex":
            b.indexer.rescan_now()
            self._json({"ok": True})
        elif path == "/task":
            self._task(self._body().get("q", ""))
        elif path == "/ask":
            self._ask(self._body().get("q", ""))
        else:
            self._json({"error": "not found"}, 404)

    def _task(self, q):
        b = self.backend
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.end_headers()

        def send(obj):
            b.model_server.touch()  # keep the model loaded while a long task streams
            self.wfile.write(f"data: {json.dumps(obj)}\n\n".encode())
            self.wfile.flush()
        try:
            if not b.llm:
                raise RuntimeError("LLA is disabled — enable it in Setup")
            target = b.resolve_target(q)
            if not target:
                raise RuntimeError("Couldn't tell which file you mean. Include its name, e.g. fix app.py it crashes on start")
            if not b.model_server._healthy():
                send({"type": "thought", "agent": "LSearch", "text": f"Loading {b.llm.name}…"})
            b.model_server.ensure()
            run_task(b.cfg["llm"] | {"agent": b.cfg["agent"]}, q, target, send)
        except (BrokenPipeError, ConnectionResetError):
            pass
        except Exception as e:
            try:
                send({"type": "error", "text": str(e)})
            except OSError:
                pass

    def _ask(self, q):
        b = self.backend
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        self.end_headers()

        def send(obj):
            self.wfile.write(f"data: {json.dumps(obj)}\n\n".encode())
            self.wfile.flush()
        try:
            if not b.llm:
                raise RuntimeError("LLM is disabled — set llm.enabled in config.json")
            b.model_server.ensure()
            for t in b.llm.stream(build_messages(b.cfg["llm"], q, b.context(q))):
                send({"t": t})
            send({"done": True})
        except (BrokenPipeError, ConnectionResetError):
            pass  # front end cancelled
        except Exception as e:
            try:
                send({"error": str(e)})
            except OSError:
                pass


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=52123)
    args = ap.parse_args()
    Handler.backend = Backend()
    ThreadingHTTPServer(("127.0.0.1", args.port), Handler).serve_forever()


if __name__ == "__main__":
    main()
