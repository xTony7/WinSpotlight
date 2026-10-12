"""SQLite metadata/FTS store plus an in-memory vector index."""
import re
import sqlite3
import threading

import numpy as np

SCHEMA = """
PRAGMA journal_mode=WAL;
CREATE TABLE IF NOT EXISTS files(
    id INTEGER PRIMARY KEY,
    path TEXT UNIQUE NOT NULL,
    name TEXT NOT NULL,
    ext TEXT,
    kind TEXT,
    size INTEGER,
    mtime REAL,
    scan_id INTEGER,
    embedded_mtime REAL DEFAULT 0
);
CREATE VIRTUAL TABLE IF NOT EXISTS files_fts USING fts5(
    tokens, tokenize='unicode61 remove_diacritics 2', prefix='1 2 3'
);
CREATE TABLE IF NOT EXISTS chunks(
    id INTEGER PRIMARY KEY,
    file_id INTEGER NOT NULL,
    text TEXT,
    vec BLOB
);
CREATE INDEX IF NOT EXISTS chunks_file ON chunks(file_id);
CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);
CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(text, tokenize='porter unicode61 remove_diacritics 2');
"""

_CAMEL = re.compile(r"(?<=[a-z0-9])(?=[A-Z])")
_SEP = re.compile(r"[\W_]+")


def name_tokens(name):
    """'MyReport_final-v2.docx' -> 'myreport_final-v2.docx my report final v2 docx'"""
    split = _SEP.sub(" ", _CAMEL.sub(" ", name)).strip().lower()
    return f"{name.lower()} {split}"


def connect(db_path):
    con = sqlite3.connect(db_path, timeout=30, check_same_thread=False)
    con.row_factory = sqlite3.Row
    con.executescript(SCHEMA)
    # One-time backfill for indexes built before content keyword search existed.
    if con.execute("SELECT 1 FROM chunks LIMIT 1").fetchone() and not con.execute("SELECT 1 FROM chunks_fts LIMIT 1").fetchone():
        con.execute("INSERT INTO chunks_fts(rowid, text) SELECT id, text FROM chunks")
        con.commit()
    return con


def ensure_model(con, model):
    """Vectors from different models can't be compared; drop them when the model changes.

    Chunk text stays, so keyword search over contents keeps working while files re-embed."""
    row = con.execute("SELECT value FROM meta WHERE key='embed_model'").fetchone()
    if row and row[0] == model:
        return False
    if row:
        con.execute("UPDATE chunks SET vec=NULL")
        con.execute("UPDATE files SET embedded_mtime=0")
    con.execute("INSERT OR REPLACE INTO meta(key, value) VALUES('embed_model', ?)", (model,))
    con.commit()
    return bool(row)


class VectorIndex:
    """Growable float32 matrix of chunk vectors; brute-force cosine search."""

    def __init__(self):
        self._lock = threading.Lock()
        self._vecs = None
        self._file_ids = np.empty(0, dtype=np.int64)
        self._chunk_ids = np.empty(0, dtype=np.int64)
        self._n = 0

    def __len__(self):
        return self._n

    def load(self, con):
        rows = con.execute("SELECT id, file_id, vec FROM chunks WHERE vec IS NOT NULL").fetchall()
        if rows:
            self.add([r["id"] for r in rows], [r["file_id"] for r in rows],
                     np.stack([np.frombuffer(r["vec"], dtype=np.float32) for r in rows]))

    def add(self, chunk_ids, file_ids, vecs):
        with self._lock:
            need = self._n + len(vecs)
            if self._vecs is None:
                cap = max(1024, need)
                self._vecs = np.zeros((cap, vecs.shape[1]), dtype=np.float32)
                self._file_ids = np.zeros(cap, dtype=np.int64)
                self._chunk_ids = np.zeros(cap, dtype=np.int64)
            elif need > len(self._vecs):
                cap = max(need, len(self._vecs) * 2)
                self._vecs = np.resize(self._vecs, (cap, self._vecs.shape[1]))
                self._file_ids = np.resize(self._file_ids, cap)
                self._chunk_ids = np.resize(self._chunk_ids, cap)
            self._vecs[self._n:need] = vecs
            self._file_ids[self._n:need] = file_ids
            self._chunk_ids[self._n:need] = chunk_ids
            self._n = need

    def remove_files(self, file_ids):
        with self._lock:
            if not self._n or not file_ids:
                return
            keep = ~np.isin(self._file_ids[:self._n], list(file_ids))
            k = int(keep.sum())
            self._vecs[:k] = self._vecs[:self._n][keep]
            self._file_ids[:k] = self._file_ids[:self._n][keep]
            self._chunk_ids[:k] = self._chunk_ids[:self._n][keep]
            self._n = k

    def search(self, q, k=40):
        """Returns [(file_id, chunk_id, score)] best chunk per file."""
        with self._lock:
            if not self._n:
                return []
            scores = self._vecs[:self._n] @ q
            top = np.argsort(-scores)[: k * 3]
            out, seen = [], set()
            for i in top:
                fid = int(self._file_ids[i])
                if fid in seen:
                    continue
                seen.add(fid)
                out.append((fid, int(self._chunk_ids[i]), float(scores[i])))
                if len(out) >= k:
                    break
            return out
