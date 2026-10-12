# LSearch — Spotlight for Windows

A Spotlight-style launcher for Windows. Press **Win+Y** to search apps, files, document contents
and browser history by meaning, not just by name. It can also fix files for you with **LLA**, a
local mixture-of-experts model: type `fix app.py it crashes on start` and press Tab.

Everything runs on your PC.

## Install

Download `LSearch-Setup.exe` from [Releases](../../releases) and run it. It installs per user,
with no admin prompt, and needs neither Python nor .NET. The setup wizard asks which folders to
index, whether to include browser history and, on PCs with 16 GB of RAM or more, whether to
download LLA (4.1 GB, from [Hugging Face](https://huggingface.co/scutoid/2x4B)).

| Key | Action |
| --- | --- |
| ↑ / ↓ | Move selection |
| ↵ | Open (copies the result for calculator rows) |
| Ctrl+↵ | Show in Explorer |
| Tab | Run an LLA task, e.g. `fix "C:\code\app.py" it crashes on start` |
| Esc | Clear, then hide |

## System requirements

| | Search only | With LLA |
| --- | --- | --- |
| OS | Windows 10 1809+ (64-bit) | same |
| RAM | 8 GB | 16 GB minimum, 32 GB recommended |
| CPU | 4 threads | 4 cores with AVX2; 8+ cores recommended |
| Disk | ~400 MB plus the index | +4.1 GB for the model |

LLA writes out its reasoning before each step, so it is slow on small CPUs (about 2 tokens/s on 4 threads).

## How it works

- **Filename index:** every fixed drive is crawled into SQLite FTS5 (system folders skipped).
- **Content index:** text, code, PDF and DOCX files under the chosen folders are chunked, embedded
  locally (`BAAI/bge-small-en-v1.5` via fastembed/ONNX) and also indexed for keyword search.
- **Ranking:** reciprocal-rank fusion of name, meaning and keyword matches. Natural-language queries
  ("whats that one doc with the lease terms") are parsed, then a cross-encoder reranks the top
  candidates. Fast results appear instantly and reranked ones about 2 s later.
- **Browser history:** Chrome, Edge, Brave, Vivaldi, Opera and Firefox, shown below local results.
- **LLA tasks:** a Fixer agent edits the file using tools (read, replace, write), then a Reviewer
  agent checks the change. Edits are confined to the target file's folder, a `.lsearch.bak` backup
  is kept, and shell commands are off unless `agent.allow_commands` is true.

Config lives at `%APPDATA%\LSearch\config.json` (tray → *Open config*).

### Using your own model

LLA runs through llama.cpp's `llama-server` on port 1114. To use another model, point `llm` at any
OpenAI-compatible server (llama.cpp, Ollama, LM Studio, vLLM):

```json
"llm": { "enabled": true, "display_name": "MyModel",
         "base_url": "http://127.0.0.1:11434/v1", "model": "my-model", "model_path": "" }
```

Or set `model_path` to a `.gguf` file and LSearch will start `llama-server` for it on demand.

## Build from source

Requires Python 3.10+, the .NET 10 SDK and, for the installer, [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```
pip install -r requirements.txt pyinstaller
powershell -File packaging\build.ps1     # -> dist\LSearch\ and dist\LSearch-Setup.exe
```

For development, `cd winui && dotnet build -c Release` and run `LSearch.exe` from `bin\`. It
starts the Python backend (`pythonw -m lsearch.server`, localhost:52123) from source. Debug flags:
`--show`, `--pin`, `--query <text>`, `--setup`.
