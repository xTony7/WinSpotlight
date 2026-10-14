"""Hybrid search: FTS5 filename match + embedding similarity, fused with reciprocal rank."""
import ast
import math
import operator
import re
import time

from . import store
from .i18n import t

SECTION_ORDER = [("app", t("Applications", "Aplicaciones")), ("document", t("Documents", "Documentos")),
                 ("code", t("Code", "Código")), ("folder", t("Folders", "Carpetas")),
                 ("image", t("Images", "Imágenes")), ("video", t("Movies", "Películas")),
                 ("audio", t("Music", "Música")), ("archive", t("Archives", "Archivos comprimidos")),
                 ("other", t("Other", "Otros"))]
PER_SECTION = 5
RRF_K = 40
RERANK_TOP = 20
RERANK_CHARS = 320  # cross-encoder cost grows with passage length; short passages keep it ~0.3 s

STOPWORDS = set("""a an the and or of to in on for with about from by at is are was were be it this that
these those my me i you your what whats which where wheres who how when one some any doc docs document
documents file files thing stuff find show open get look looking need has have had says said contains
containing mentioning called named like""".split())
KIND_WORDS = {"doc": "document", "docs": "document", "document": "document", "pdf": "document",
              "note": "document", "notes": "document", "spreadsheet": "document", "sheet": "document",
              "essay": "document", "report": "document", "script": "code", "code": "code",
              "image": "image", "photo": "image", "picture": "image", "screenshot": "image",
              "folder": "folder", "app": "app", "video": "video", "song": "audio"}
_LEAD = re.compile(r"^(?:what'?s|whats|what is|what was|where'?s|wheres|where is|find|show me|open|get|"
                   r"search for|search|look(?:ing)? for|i need|i want|that|this|the|my|one|a|an|some|"
                   r"there was|there's|theres)\b\s*", re.I)
_LINK = re.compile(r"^(?:with|about|containing|that has|that had|that says|that said|that mentions|"
                   r"mentioning|where|on|for|called|named|regarding|which has)\b\s*", re.I)


def kind_factor(wanted, kind):
    """Boost results of the requested kind; "doc" is loose enough to include code/text files."""
    if kind == wanted:
        return 1.5
    if wanted == "document" and kind == "code":
        return 0.9
    return 0.3


def parse_query(q):
    """'whats that one doc with the lease terms' -> ('the lease terms', 'document', True).

    Returns (core terms, requested kind or None, whether it reads as natural language)."""
    s, kind, stripped = q.strip(), None, False
    while (m := _LEAD.match(s)) and m.end():
        s, stripped = s[m.end():], True
    words = s.split(maxsplit=1)
    if len(words) > 1:
        w = words[0].lower()
        k = KIND_WORDS.get(w) or KIND_WORDS.get(w.rstrip("s"))
        if k:
            kind, s, stripped = k, words[1], True
    while (m := _LINK.match(s)) and m.end():
        s, stripped = s[m.end():], True
    s = s.strip(" ?.!") or q.strip()
    return s, kind, stripped or len(q.split()) >= 4

_OPS = {ast.Add: operator.add, ast.Sub: operator.sub, ast.Mult: operator.mul,
        ast.Div: operator.truediv, ast.Pow: operator.pow, ast.Mod: operator.mod,
        ast.FloorDiv: operator.floordiv, ast.USub: operator.neg, ast.UAdd: operator.pos}
_FUNCS = {k: getattr(math, k) for k in ("sqrt", "sin", "cos", "tan", "log", "log10", "exp", "floor", "ceil")}
_CONSTS = {"pi": math.pi, "e": math.e}


def calculate(expr):
    """Safe arithmetic evaluation, like Spotlight's inline calculator."""
    if not re.search(r"\d", expr) or not re.fullmatch(r"[\d\s.+\-*/%^()a-z,]+", expr):
        return None

    def ev(n):
        if isinstance(n, ast.Constant) and isinstance(n.value, (int, float)):
            return n.value
        if isinstance(n, ast.BinOp) and type(n.op) in _OPS:
            return _OPS[type(n.op)](ev(n.left), ev(n.right))
        if isinstance(n, ast.UnaryOp) and type(n.op) in _OPS:
            return _OPS[type(n.op)](ev(n.operand))
        if isinstance(n, ast.Call) and isinstance(n.func, ast.Name) and n.func.id in _FUNCS:
            return _FUNCS[n.func.id](*[ev(a) for a in n.args])
        if isinstance(n, ast.Name) and n.id in _CONSTS:
            return _CONSTS[n.id]
        raise ValueError
    try:
        tree = ast.parse(expr.replace("^", "**"), mode="eval")
        if isinstance(tree.body, ast.Constant):
            return None
        v = ev(tree.body)
    except Exception:
        return None
    return f"{v:,.10g}" if isinstance(v, float) else f"{v:,}"


class SearchEngine:
    def __init__(self, con, embedder, vindex):
        self.con, self.embedder, self.vindex = con, embedder, vindex
        # Similarity cut-offs depend on the model's score distribution (bge scores noise ~0.5).
        self.sem_min = embedder.cfg.get("min_similarity", 0.62)
        self.sem_spread = embedder.cfg.get("similarity_spread", 0.08)

    def _fts(self, query, limit=80):
        toks = re.findall(r"\w+", query.lower())
        if not toks:
            return []
        match = " ".join(f'"{t}"*' for t in toks)
        try:
            return self.con.execute(
                "SELECT f.*, bm25(files_fts) AS score FROM files_fts JOIN files f ON f.id = files_fts.rowid "
                "WHERE files_fts MATCH ? ORDER BY score LIMIT ?", (match, limit)).fetchall()
        except Exception:
            return []

    def _semantic(self, query, limit=40):
        if not self.embedder.ready or len(self.vindex) == 0 or len(query) < 3:
            return []
        try:
            q = self.embedder.query(query)
        except Exception:
            return []
        hits = self.vindex.search(q, limit)
        # bge-style models score unrelated text ~0.5; keep only clearly relevant hits.
        best = hits[0][2] if hits else 0
        hits = [h for h in hits if h[2] > self.sem_min and h[2] > best - self.sem_spread]
        if not hits:
            return []
        fids = [h[0] for h in hits]
        qs = ",".join("?" * len(fids))
        files = {r["id"]: r for r in self.con.execute(f"SELECT * FROM files WHERE id IN ({qs})", fids)}
        cids = [h[1] for h in hits]
        texts = {r[0]: r[1] for r in self.con.execute(
            f"SELECT id, text FROM chunks WHERE id IN ({','.join('?' * len(cids))})", cids)}
        return [(files[f], texts.get(c, ""), s) for f, c, s in hits if f in files]

    def _content_fts(self, terms, limit=40):
        """BM25 over document text: best chunk per file for any of the key terms."""
        toks = [t for t in re.findall(r"\w+", terms.lower()) if t not in STOPWORDS and len(t) > 1]
        if not toks:
            return []
        match = " OR ".join(f'"{t}"' for t in toks)
        try:
            rows = self.con.execute(
                "SELECT c.file_id, c.text, bm25(chunks_fts) AS s FROM chunks_fts "
                "JOIN chunks c ON c.id = chunks_fts.rowid WHERE chunks_fts MATCH ? ORDER BY s LIMIT ?",
                (match, limit * 3)).fetchall()
        except Exception:
            return []
        best = {}
        for r in rows:
            best.setdefault(r["file_id"], r["text"])
        fids = list(best)[:limit]
        if not fids:
            return []
        files = {r["id"]: r for r in self.con.execute(
            f"SELECT * FROM files WHERE id IN ({','.join('?' * len(fids))})", fids)}
        return [(files[f], best[f]) for f in fids if f in files]

    def search(self, query, rerank=True):
        """Returns (top_hit, [(section_title, [result,...])], calc_result)."""
        query = query.strip()
        if not query:
            return None, [], None
        calc = calculate(query.lower())
        core, want_kind, natural = parse_query(query)
        scored = {}

        def hit(row):
            r = scored.get(row["id"])
            if r is None:
                r = scored[row["id"]] = {"id": row["id"], "path": row["path"], "name": row["name"],
                                         "kind": row["kind"], "size": row["size"], "mtime": row["mtime"],
                                         "snippet": "", "score": 0.0}
            return r

        def set_snippet(r, text):
            if not r["snippet"] and ":" in text:
                r["snippet"] = text.split(":", 1)[1].strip()

        cl = core.lower()
        name_q = " ".join(t for t in core.split() if t.lower() not in STOPWORDS) if natural else core
        for rank, row in enumerate(self._fts(name_q or core)):
            r = hit(row)
            r["score"] += 1 / (RRF_K + rank)
            r["fts"] = True
            stem = row["name"].lower().rsplit(".", 1)[0] if row["kind"] != "folder" else row["name"].lower()
            if stem == cl:
                r["score"] += 0.03
            elif stem.startswith(cl):
                r["score"] += 0.015
        if not calc:
            for rank, (row, text, sim) in enumerate(self._semantic(core)):
                r = hit(row)
                r["score"] += 1 / (RRF_K + rank) * (0.5 + sim)
                set_snippet(r, text)
            if natural or len(core) >= 4:
                for rank, (row, text) in enumerate(self._content_fts(core)):
                    r = hit(row)
                    r["score"] += 1 / (RRF_K + rank)
                    set_snippet(r, text)

        # Natural-language queries ("that doc with the Q3 numbers"): a cross-encoder reads each
        # candidate's text against the query, and its judgement dominates the ranking.
        if rerank and natural and not calc and self.embedder.reranker and scored:
            # Only results whose contents we actually read are judged; a bare name like
            # "moe.exe" shouldn't beat the document that discusses the MoE model.
            cands = [r for r in sorted(scored.values(), key=lambda r: -r["score"]) if r["snippet"]][:RERANK_TOP]
            for r in scored.values():
                r["score"] *= 0.3
            docs = [f"{r['name']}. {r['snippet']}"[:RERANK_CHARS] for r in cands]
            try:
                scores = self.embedder.rerank(core, docs) if cands else []
                order = sorted(range(len(cands)), key=lambda i: -scores[i])
                for rank, i in enumerate(order):  # rank-based: raw logits are poorly calibrated
                    cands[i]["score"] += 2.5 / (RRF_K + rank) + 1 / (1 + math.exp(-scores[i]))
            except Exception:
                pass

        now = time.time()
        for r in scored.values():
            if r["kind"] == "app" and r.get("fts"):
                r["score"] *= 1.6
            if want_kind:
                r["score"] *= kind_factor(want_kind, r["kind"])
            age_days = (now - (r["mtime"] or 0)) / 86400
            r["score"] *= 1 + 0.15 * math.exp(-age_days / 30)  # gentle recency boost

        ranked = sorted(scored.values(), key=lambda r: -r["score"])
        if not ranked:
            return None, [], calc
        top, rest = ranked[0], ranked[1:]
        sections = []
        for kind, title in SECTION_ORDER:
            items = [r for r in rest if r["kind"] == kind][:PER_SECTION]
            if items:
                sections.append((title, items))
        sections.sort(key=lambda s: -s[1][0]["score"])
        return top, sections, calc

    def context_for(self, results, max_chars=6000):
        """Build RAG context for the LLM from the given result dicts."""
        parts, used = [], 0
        for r in results:
            rows = self.con.execute(
                "SELECT text FROM chunks WHERE file_id=? ORDER BY id LIMIT 4", (r["id"],)).fetchall()
            body = "\n".join(x[0].split(":", 1)[-1].strip() for x in rows[1:]) or r.get("snippet", "")
            block = f"### {r['name']}  ({r['path']})\n{body}".strip()
            if used + len(block) > max_chars:
                block = block[: max(0, max_chars - used)]
            parts.append(block)
            used += len(block)
            if used >= max_chars:
                break
        return "\n\n".join(parts)
