"""Embedding backends. All return L2-normalised float32 arrays."""
import json
import threading
import time
import urllib.request

import numpy as np


def _normalise(a):
    a = np.asarray(a, dtype=np.float32)
    n = np.linalg.norm(a, axis=1, keepdims=True)
    n[n == 0] = 1
    return a / n


class FastEmbedBackend:
    def __init__(self, model):
        from fastembed import TextEmbedding
        self.model = TextEmbedding(model)

    def passages(self, texts):
        return _normalise(list(self.model.passage_embed(texts, batch_size=32)))

    def query(self, text):
        return _normalise(list(self.model.query_embed(text)))[0]


class OllamaBackend:
    def __init__(self, model, url):
        self.model, self.url = model, url.rstrip("/")

    def _embed(self, texts):
        req = urllib.request.Request(
            self.url + "/api/embed",
            data=json.dumps({"model": self.model, "input": texts}).encode(),
            headers={"Content-Type": "application/json"},
        )
        with urllib.request.urlopen(req, timeout=120) as r:
            return _normalise(json.loads(r.read())["embeddings"])

    @property
    def gemma(self):
        return "embeddinggemma" in self.model

    def passages(self, texts):
        if self.gemma:  # EmbeddingGemma's document format; WinSpotlight chunks are "filename: content"
            texts = [f"title: {t.split(': ', 1)[0]} | text: {t.split(': ', 1)[-1]}" for t in texts]
        return self._embed(list(texts))

    def query(self, text):
        return self._embed([f"task: search result | query: {text}" if self.gemma else text])[0]


class Embedder:
    """Lazily loads the configured backend; safe to share across threads."""

    def __init__(self, cfg):
        self.cfg = cfg
        self.backend = None
        self.reranker = None
        self.error = None
        self.last_search = 0.0  # background indexing yields the CPU while the user is searching
        self._lock = threading.Lock()

    @property
    def ready(self):
        return self.backend is not None

    def load(self):
        with self._lock:
            if self.backend or self.error:
                return
            try:
                if self.cfg["backend"] == "ollama":
                    self.backend = OllamaBackend(self.cfg["model"], self.cfg["ollama_url"])
                else:
                    self.backend = FastEmbedBackend(self.cfg["model"])
            except Exception as e:  # surfaced in the UI footer
                self.error = str(e)
            if self.cfg.get("reranker"):
                try:
                    from fastembed.rerank.cross_encoder import TextCrossEncoder
                    self.reranker = TextCrossEncoder(self.cfg["reranker"])
                except Exception:
                    self.reranker = None  # search still works, just without reranking

    def passages(self, texts):
        return self.backend.passages(texts)

    def query(self, text):
        self.last_search = time.time()
        return self.backend.query(text)

    def rerank(self, query, docs):
        self.last_search = time.time()
        return list(self.reranker.rerank(query, docs, batch_size=32))
