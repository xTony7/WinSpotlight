"""Runs a local GGUF model (e.g. LLA) with llama.cpp's llama-server on the configured port.

Started lazily on the first task so the model doesn't hold ~4 GB of RAM while unused, and
stopped again after `idle_minutes` without requests. If something is already listening on the
port (a server you started yourself), it is used as-is.
"""
import atexit
import os
import subprocess
import threading
import time
import urllib.request
from urllib.parse import urlparse

DEFAULT_EXE = os.path.join(os.environ.get("LOCALAPPDATA", ""), "LSearch", "llama", "llama-server.exe")


class LlamaServer:
    def __init__(self, cfg):
        self.cfg = cfg
        u = urlparse(cfg["base_url"])
        self.host, self.port = u.hostname or "127.0.0.1", u.port or (443 if u.scheme == "https" else 80)
        self.proc = None
        self.last_used = 0.0
        self.lock = threading.Lock()
        atexit.register(self.stop)
        threading.Thread(target=self._reaper, daemon=True).start()

    @property
    def managed(self):
        return bool(self.cfg.get("model_path"))

    def _healthy(self):
        try:
            # /models exists on llama-server, Ollama and other OpenAI-compatible servers
            with urllib.request.urlopen(self.cfg["base_url"].rstrip("/") + "/models", timeout=5) as r:
                return r.status == 200
        except Exception:
            return False

    def ensure(self, timeout=180):
        """Make sure the model server is up; raises RuntimeError with a readable reason if not."""
        self.last_used = time.time()
        if self._healthy():
            return
        if not self.managed:
            raise RuntimeError(f"No model server on port {self.port}. Set llm.model_path to a .gguf "
                               "file, or start your own OpenAI-compatible server there.")
        with self.lock:
            if self.proc is None or self.proc.poll() is not None:
                exe = self.cfg.get("server_path") or DEFAULT_EXE
                model = self.cfg["model_path"]
                if not os.path.isfile(exe):
                    raise RuntimeError(f"llama-server not found at {exe}")
                if not os.path.isfile(model):
                    raise RuntimeError(f"Model file not found: {model}")
                args = [exe, "-m", model, "--host", self.host, "--port", str(self.port),
                        "-c", str(self.cfg.get("ctx_size", 8192)), "--jinja", "--alias", self.cfg["model"]]
                if self.cfg.get("threads"):
                    args += ["-t", str(self.cfg["threads"])]
                log = open(os.path.join(os.environ.get("TEMP", "."), "lsearch-llama.log"), "w")
                self.proc = subprocess.Popen(args, stdout=log, stderr=subprocess.STDOUT,
                                             creationflags=subprocess.CREATE_NO_WINDOW)
            deadline = time.time() + timeout
            while time.time() < deadline:
                if self._healthy():
                    return
                if self.proc.poll() is not None:
                    raise RuntimeError("llama-server exited while loading the model; see %TEMP%\\lsearch-llama.log")
                time.sleep(0.5)
            raise RuntimeError("Model took too long to load")

    def touch(self):
        self.last_used = time.time()

    def stop(self):
        if self.proc and self.proc.poll() is None:
            self.proc.terminate()
        self.proc = None

    def _reaper(self):
        idle = self.cfg.get("idle_minutes", 15) * 60
        while True:
            time.sleep(30)
            if self.proc and idle > 0 and time.time() - self.last_used > idle:
                with self.lock:
                    self.stop()
