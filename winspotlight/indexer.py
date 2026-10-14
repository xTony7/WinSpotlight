"""Background crawler: filename index for every fixed drive, embeddings for document contents."""
import ctypes
import os
import time
from pathlib import Path

import threading

from . import store
from .config import DB_PATH
from .i18n import t

FILE_ATTRIBUTE_REPARSE_POINT = 0x400
DRIVE_FIXED = 3

KINDS = {
    "document": {".txt", ".md", ".pdf", ".doc", ".docx", ".rtf", ".odt", ".xls", ".xlsx", ".csv",
                 ".ppt", ".pptx", ".json", ".xml", ".yaml", ".yml", ".ini", ".log", ".tex", ".html", ".htm"},
    "code": {".py", ".js", ".ts", ".tsx", ".jsx", ".java", ".c", ".cpp", ".h", ".hpp", ".cs", ".go",
             ".rs", ".rb", ".php", ".swift", ".kt", ".sh", ".ps1", ".bat", ".sql", ".lua", ".ipynb", ".toml"},
    "image": {".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".heic", ".tiff", ".ico", ".psd"},
    "video": {".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv"},
    "audio": {".mp3", ".wav", ".flac", ".m4a", ".ogg", ".aac"},
    "archive": {".zip", ".rar", ".7z", ".tar", ".gz", ".iso"},
    "app": {".exe", ".lnk", ".url", ".msi", ".appref-ms"},
}
EXT_KIND = {e: k for k, exts in KINDS.items() for e in exts}

TEXT_EXTS = (KINDS["document"] | KINDS["code"]) - {".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".rtf"}
EMBED_EXTS = TEXT_EXTS | {".pdf", ".docx"}

CHUNK_CHARS, CHUNK_OVERLAP, MAX_CHUNKS = 1000, 150, 24


def kind_of(ext, is_dir=False):
    return "folder" if is_dir else EXT_KIND.get(ext, "other")


def fixed_drives():
    get_type = ctypes.windll.kernel32.GetDriveTypeW
    return [d for d in os.listdrives() if get_type(d) == DRIVE_FIXED]


def start_menu_dirs():
    return [
        Path(os.environ.get("ProgramData", r"C:\ProgramData")) / r"Microsoft\Windows\Start Menu\Programs",
        Path(os.environ.get("APPDATA", "")) / r"Microsoft\Windows\Start Menu\Programs",
    ]


def extract_text(path, ext):
    try:
        if ext in TEXT_EXTS:
            with open(path, "r", encoding="utf-8", errors="ignore") as f:
                return f.read(200_000)
        if ext == ".pdf":
            from pypdf import PdfReader
            reader = PdfReader(path)
            return "\n".join((p.extract_text() or "") for p in reader.pages[:30])
        if ext == ".docx":
            import docx
            return "\n".join(p.text for p in docx.Document(path).paragraphs)
    except Exception:
        return ""
    return ""


def chunk(text):
    text = " ".join(text.split())
    out, i = [], 0
    while i < len(text) and len(out) < MAX_CHUNKS:
        out.append(text[i:i + CHUNK_CHARS])
        i += CHUNK_CHARS - CHUNK_OVERLAP
    return out


class Indexer(threading.Thread):
    """Runs forever in the background; reports progress through on_status(str)."""

    def __init__(self, cfg, embedder, vindex, on_status=lambda s: None):
        super().__init__(daemon=True)
        self.cfg, self.embedder, self.vindex = cfg, embedder, vindex
        self.last_status = ""
        self._on_status = on_status
        self._stop = False
        self._force = False

    def _emit(self, text):
        self.last_status = text
        self._on_status(text)

    def stop(self):
        self._stop = True

    def rescan_now(self):
        self._force = True

    # ---- main loop -------------------------------------------------------
    def run(self):
        self.con = store.connect(DB_PATH)
        self._emit(t("Loading embedding model…", "Cargando el modelo de embeddings…"))
        self.embedder.load()
        if self.embedder.error:
            self._emit(t("Embeddings unavailable: ", "Embeddings no disponibles: ") + self.embedder.error[:80])
        while not self._stop:
            t0 = time.time()
            self.scan()
            if self.embedder.ready:
                self.embed_pending()
            n = self.con.execute("SELECT COUNT(*) FROM files").fetchone()[0]
            self._emit(t(f"{n:,} items indexed · {len(self.vindex):,} passages · {time.time() - t0:.0f}s",
                         f"{n:,} elementos indexados · {len(self.vindex):,} fragmentos · {time.time() - t0:.0f} s"))
            deadline = time.time() + self.cfg["rescan_minutes"] * 60
            while not self._stop and not self._force and time.time() < deadline:
                time.sleep(0.5)
            self._force = False

    # ---- filename crawl --------------------------------------------------
    def scan(self):
        scan_id = int(time.time())
        excl_names = {n.lower() for n in self.cfg["exclude_dir_names"]}
        excl_paths = tuple(os.path.normcase(p) for p in self.cfg["exclude_paths"])
        batch = []

        def flush():
            cur = self.con.cursor()
            for path, name, ext, kind, size, mtime in batch:
                row = cur.execute("SELECT id, mtime FROM files WHERE path=?", (path,)).fetchone()
                if row:
                    cur.execute("UPDATE files SET scan_id=?, size=?, mtime=? WHERE id=?",
                                (scan_id, size, mtime, row["id"]))
                else:
                    cur.execute("INSERT INTO files(path,name,ext,kind,size,mtime,scan_id) VALUES(?,?,?,?,?,?,?)",
                                (path, name, ext, kind, size, mtime, scan_id))
                    cur.execute("INSERT INTO files_fts(rowid, tokens) VALUES(?,?)",
                                (cur.lastrowid, store.name_tokens(Path(name).stem if kind == "app" else name)))
            self.con.commit()
            batch.clear()

        # Applications from the Start Menu are indexed first so they're searchable immediately.
        for root in start_menu_dirs():
            for p in root.rglob("*"):
                if p.suffix.lower() in (".lnk", ".url", ".appref-ms"):
                    try:
                        st = p.stat()
                    except OSError:
                        continue
                    batch.append((str(p), p.stem, p.suffix.lower(), "app", st.st_size, st.st_mtime))
        flush()

        count = 0
        roots = fixed_drives() if self.cfg["index_all_drives"] else             [r for r in self.cfg["content_roots"] if os.path.isdir(r)]
        for drive in roots:
            stack = [drive]
            while stack and not self._stop:
                while time.time() - self.embedder.last_search < 2 and not self._stop:
                    time.sleep(0.1)  # yield the GIL/CPU to interactive searches
                d = stack.pop()
                try:
                    it = os.scandir(d)
                except OSError:
                    continue
                with it:
                    for e in it:
                        try:
                            st = e.stat(follow_symlinks=False)
                            if st.st_file_attributes & FILE_ATTRIBUTE_REPARSE_POINT:
                                continue
                            is_dir = e.is_dir(follow_symlinks=False)
                        except OSError:
                            continue
                        if is_dir:
                            if e.name.lower() in excl_names or e.name.startswith("."):
                                continue
                            if excl_paths and os.path.normcase(e.path).startswith(excl_paths):
                                continue
                            stack.append(e.path)
                        ext = "" if is_dir else os.path.splitext(e.name)[1].lower()
                        batch.append((e.path, e.name, ext, kind_of(ext, is_dir), st.st_size, st.st_mtime))
                        count += 1
                if len(batch) >= 5000:
                    flush()
                    self._emit(t(f"Scanning {drive} · {count:,} items…", f"Explorando {drive} · {count:,} elementos…"))
        flush()
        if self._stop:
            return

        # Drop files that disappeared since the last full scan.
        gone = [r[0] for r in self.con.execute("SELECT id FROM files WHERE scan_id != ?", (scan_id,))]
        for i in range(0, len(gone), 900):
            ids = gone[i:i + 900]
            q = ",".join("?" * len(ids))
            self.con.execute(f"DELETE FROM files_fts WHERE rowid IN ({q})", ids)
            self.con.execute(f"DELETE FROM chunks_fts WHERE rowid IN (SELECT id FROM chunks WHERE file_id IN ({q}))", ids)
            self.con.execute(f"DELETE FROM chunks WHERE file_id IN ({q})", ids)
            self.con.execute(f"DELETE FROM files WHERE id IN ({q})", ids)
        self.con.commit()
        self.vindex.remove_files(set(gone))

    # ---- semantic embedding ---------------------------------------------
    def embed_pending(self):
        roots = [os.path.normcase(r) for r in self.cfg["content_roots"]]
        rows = self.con.execute(
            "SELECT id, path, name, ext, kind, size, mtime FROM files "
            "WHERE embedded_mtime != mtime AND (kind='app' OR kind IN ('document','code','folder'))"
        ).fetchall()
        todo = []
        for r in rows:
            if r["kind"] == "app":
                todo.append(r)
            elif os.path.normcase(r["path"]).startswith(tuple(roots)):
                if r["kind"] == "folder" or (r["ext"] in EMBED_EXTS and r["size"] <= self.cfg["content_max_bytes"]):
                    todo.append(r)
        total = len(todo)
        for n, r in enumerate(todo, 1):
            if self._stop:
                return
            while time.time() - self.embedder.last_search < 2 and not self._stop:
                time.sleep(0.1)  # keep search snappy: don't compete with it for CPU
            label = f"{r['name']} — {Path(r['path']).parent.name}"
            texts = [label]  # name-only passage lets "browser" find "Google Chrome", etc.
            if r["kind"] in ("document", "code"):
                texts += [f"{r['name']}: {c}" for c in chunk(extract_text(r["path"], r["ext"]))]
            try:
                vecs = self.embedder.passages(texts)
            except Exception:
                continue
            old = [x[0] for x in self.con.execute("SELECT id FROM chunks WHERE file_id=?", (r["id"],))]
            if old:
                self.con.execute("DELETE FROM chunks_fts WHERE rowid IN (SELECT id FROM chunks WHERE file_id=?)", (r["id"],))
                self.con.execute("DELETE FROM chunks WHERE file_id=?", (r["id"],))
                self.vindex.remove_files({r["id"]})
            ids = []
            for t, v in zip(texts, vecs):
                cur = self.con.execute("INSERT INTO chunks(file_id, text, vec) VALUES(?,?,?)",
                                       (r["id"], t, v.tobytes()))
                ids.append(cur.lastrowid)
                self.con.execute("INSERT INTO chunks_fts(rowid, text) VALUES(?,?)", (cur.lastrowid, t))
            self.con.execute("UPDATE files SET embedded_mtime=? WHERE id=?", (r["mtime"], r["id"]))
            self.vindex.add(ids, [r["id"]] * len(ids), vecs)
            if n % 25 == 0:
                self.con.commit()
                self._emit(t("Embedding ", "Procesando ") + f"{n:,}/{total:,} · {r['name'][:40]}")
        self.con.commit()
