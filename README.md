**English** · [Español](README.es.md)

# WinSpotlight — Spotlight for Windows

> [!WARNING]
> **Known security issue — not maintained.** In this version the local search backend accepts
> requests from any program on the PC, including web pages open in a browser, which could ask it to
> open files or programs. This repository is archived at version 1.1.0 and will not receive fixes.
> Use it at your own risk, ideally only for study.

A Spotlight-style launcher for Windows with the look of macOS. Press **Alt+Space** to search apps,
files, document contents and browser history by meaning, not just by name. It can also fix files
for you with **LLA**, a local mixture-of-experts model: type `fix app.py it crashes on start` and
press Tab.

Everything runs on your PC.

WinSpotlight is a fork of [LSearch](https://github.com/blueprintnerd/LSearch) by blueprintnerd.
This fork adds:

- A frosted-glass window modeled on macOS Spotlight (capsule search field, Liquid Glass edge,
  soft shadow, accent-colored selection).
- An **Appearance** panel: theme, accent color, blur, transparency, vibrancy and more.
- An English and Spanish interface that follows the Windows display language.
- **Alt+Space** as the default hotkey (was Win+Y).

## Install

Download `WinSpotlight-Setup.exe` from [Releases](../../releases) when one is published, or
[build from source](#build-from-source). The installer works per user, with no admin prompt, and
needs neither Python nor .NET. The setup wizard asks which folders to index, whether to include
browser history and, on PCs with 16 GB of RAM or more, whether to download LLA (4.1 GB, from
[Hugging Face](https://huggingface.co/scutoid/2x4B)).

| Key | Action |
| --- | --- |
| Alt+Space | Show or hide WinSpotlight |
| ↑ / ↓ | Move selection |
| ↵ | Open (copies the result for calculator rows) |
| Ctrl+↵ | Show in Explorer |
| Tab | Run an LLA task, e.g. `fix "C:\code\app.py" it crashes on start` |
| Ctrl+, | Appearance settings |
| Esc | Clear, then hide |

**Coming from LSearch?** On first start WinSpotlight moves your config, index and models from
`%APPDATA%\LSearch` and `%LOCALAPPDATA%\LSearch`. Uninstall LSearch afterwards, since both
apps want the same hotkey.

## System requirements

| | Search only | With LLA |
| --- | --- | --- |
| OS | Windows 10 1809+ (64-bit) | same |
| RAM | 8 GB | 16 GB minimum, 32 GB recommended |
| CPU | 4 threads | 4 cores with AVX2; 8+ cores recommended |
| Disk | ~400 MB plus the index | +4.1 GB for the model |

LLA writes out its reasoning before each step, so it is slow on small CPUs (about 2 tokens/s on 4 threads).

## Appearance

Click the gear in the search bar, press Ctrl+, or use tray → *Appearance…*:

| Setting | What it does |
| --- | --- |
| Mode | Auto (follows Windows), Light or Dark |
| Accent color | The eight macOS accents; used for selection, switches and sliders |
| Blur | How strongly the desktop behind the glass is blurred (0 = clear glass) |
| Transparency | How much of the desktop shows through the tint |
| Vibrancy | Saturation boost of what shows through, as on macOS |
| Edge highlight | The bright Liquid Glass rim |
| Shadow | Soft shadow around the window |
| Show preview pane | File preview to the right of the results |

Changes apply live and are saved under `appearance` in config.json. The glass frosts a snapshot of
the desktop taken each time the window opens, so video playing behind it stays still until the next
open.

## Language

The interface, setup wizard and installer are in English or Spanish, following the Windows display
language. To force one, add `"language": "en"` or `"language": "es"` to config.json and restart
WinSpotlight.

## How it works

- **Filename index:** every fixed drive is crawled into SQLite FTS5 (system folders skipped).
- **Content index:** text, code, PDF and DOCX files under the chosen folders are chunked, embedded
  locally (`BAAI/bge-small-en-v1.5` via fastembed/ONNX) and also indexed for keyword search.
- **Ranking:** reciprocal-rank fusion of name, meaning and keyword matches. Natural-language queries
  ("whats that one doc with the lease terms") are parsed, then a cross-encoder reranks the top
  candidates. Fast results appear instantly and reranked ones about 2 s later.
- **Browser history:** Chrome, Edge, Brave, Vivaldi, Opera and Firefox, shown below local results.
- **LLA tasks:** a Fixer agent edits the file using tools (read, replace, write), then a Reviewer
  agent checks the change. Edits are confined to the target file's folder, a `.winspotlight.bak`
  backup is kept, and shell commands are off unless `agent.allow_commands` is true.

Config lives at `%APPDATA%\WinSpotlight\config.json` (tray → *Open config*).

### Using your own model

LLA runs through llama.cpp's `llama-server` on port 1114. To use another model, point `llm` at any
OpenAI-compatible server (llama.cpp, Ollama, LM Studio, vLLM):

```json
"llm": { "enabled": true, "display_name": "MyModel",
         "base_url": "http://127.0.0.1:11434/v1", "model": "my-model", "model_path": "" }
```

Or set `model_path` to a `.gguf` file and WinSpotlight will start `llama-server` for it on demand.

## Build from source

Requires Python 3.10+, the .NET 10 SDK and, for the installer, [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```
pip install -r requirements.txt pyinstaller
powershell -File packaging\build.ps1     # -> dist\WinSpotlight\ and dist\WinSpotlight-Setup.exe
```

For development, `cd winui && dotnet build -c Release` and run `WinSpotlight.exe` from `bin\`. It
starts the Python backend (`pythonw -m winspotlight.server`, localhost:52123) from source. Debug
flags: `--show`, `--pin`, `--query <text>`, `--setup`.

## License

Apache License 2.0 — see [LICENSE](LICENSE). Based on LSearch by blueprintnerd; changes in this
fork are listed in the commit history.
