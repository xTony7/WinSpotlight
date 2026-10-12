"""User configuration, stored at %APPDATA%\\LSearch\\config.json."""
import json
import os
from pathlib import Path

APP_DIR = Path(os.environ.get("APPDATA", Path.home())) / "LSearch"
CONFIG_PATH = APP_DIR / "config.json"
DB_PATH = APP_DIR / "index.db"

HOME = str(Path.home())

DEFAULTS = {
    # Global hotkey. Modifiers: alt, ctrl, shift, win. Key: any single key name or "space".
    "hotkey": "win+y",
    # Set by the setup wizard once it has run.
    "setup_complete": False,
    "browser_history": True,
    # True: file names on every fixed drive are searchable. False: only inside content_roots.
    "index_all_drives": True,
    # Filename index covers every fixed drive except these directory names / path prefixes.
    "exclude_dir_names": [
        "$Recycle.Bin", "System Volume Information", "Windows", "Program Files",
        "Program Files (x86)", "ProgramData", "AppData", "node_modules", ".git",
        "__pycache__", ".venv", "venv", "site-packages", ".cache", "$WinREAgent",
        "Recovery", "PerfLogs", "MSOCache", ".gradle", ".m2", ".nuget", ".cargo",
    ],
    "exclude_paths": [],
    # File *contents* are embedded for semantic search only under these roots.
    "content_roots": [
        os.path.join(HOME, d) for d in ("Desktop", "Documents", "Downloads", "OneDrive")
    ],
    "content_max_bytes": 5_000_000,
    "rescan_minutes": 30,
    "embedding": {
        # "fastembed" runs fully local via ONNX; "ollama" calls an Ollama server's /api/embed.
        "backend": "fastembed",
        "model": "BAAI/bge-small-en-v1.5",
        # Cross-encoder that rescores the top candidates for natural-language queries. "" disables.
        "reranker": "Xenova/ms-marco-MiniLM-L-6-v2",
        "ollama_url": "http://127.0.0.1:11434",
    },
    # LLA task agents ("fix <file> it does <problem>"). Edits stay inside the target file's folder
    # and every edited file is backed up as <name>.lsearch.bak first.
    "agent": {"allow_commands": False},
    # Local LLM slot. Any OpenAI-compatible server works: llama.cpp `llama-server`,
    # LM Studio, vLLM, Ollama (base_url .../v1), text-generation-webui, etc.
    "llm": {
        "enabled": False,
        "provider": "openai_compat",
        "display_name": "Assistant",
        "base_url": "http://127.0.0.1:1114/v1",
        "model": "my-finetune",
        "api_key": "",
        "temperature": 0.3,
        "max_tokens": 4096,  # reasoning models spend tokens thinking before they answer
        "model_path": "",  # a .gguf here makes LSearch run it with llama-server on base_url's port
        "server_path": "",  # default: %LOCALAPPDATA%\LSearch\llama\llama-server.exe
        "ctx_size": 8192,
        "threads": 0,  # 0 = llama.cpp default
        "idle_minutes": 15,  # unload the model after this long unused
        "use_search_context": True,
        "system_prompt": (
            "You are a helpful assistant built into a desktop search launcher. "
            "Answer concisely. When file excerpts are provided, ground your answer in them "
            "and cite file names."
        ),
    },
}


def _merge(base, over):
    out = dict(base)
    for k, v in over.items():
        out[k] = _merge(base[k], v) if isinstance(v, dict) and isinstance(base.get(k), dict) else v
    return out


def load():
    APP_DIR.mkdir(parents=True, exist_ok=True)
    user = {}
    if CONFIG_PATH.exists():
        try:
            user = json.loads(CONFIG_PATH.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            pass
    cfg = _merge(DEFAULTS, user)
    if not CONFIG_PATH.exists():
        CONFIG_PATH.write_text(json.dumps(cfg, indent=2), encoding="utf-8")
    return cfg
