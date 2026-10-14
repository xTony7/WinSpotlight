"""Local LLM integration slot.

Plug your fine-tuned model in either by:
  1. Serving it behind any OpenAI-compatible endpoint (llama.cpp `llama-server`, vLLM, LM Studio,
     Ollama at http://host:11434/v1, ...) and setting `llm.base_url` / `llm.model` in config.json; or
  2. Subclassing `LLMProvider` (e.g. to load it in-process with transformers / llama-cpp-python)
     and registering it in `PROVIDERS`, then setting `llm.provider` to its key.
"""
import json
import urllib.request



class LLMProvider:
    name = "LLM"

    def __init__(self, cfg):
        self.cfg = cfg
        self.name = cfg.get("display_name", self.name)

    def stream(self, messages):
        """Yield text deltas for an OpenAI-style message list."""
        raise NotImplementedError


class OpenAICompatProvider(LLMProvider):
    def stream(self, messages):
        body = {"model": self.cfg["model"], "messages": messages, "stream": True,
                "temperature": self.cfg["temperature"], "max_tokens": self.cfg["max_tokens"]}
        headers = {"Content-Type": "application/json"}
        if self.cfg.get("api_key"):
            headers["Authorization"] = f"Bearer {self.cfg['api_key']}"
        req = urllib.request.Request(self.cfg["base_url"].rstrip("/") + "/chat/completions",
                                     data=json.dumps(body).encode(), headers=headers)
        with urllib.request.urlopen(req, timeout=300) as resp:
            for raw in resp:
                line = raw.decode("utf-8", "ignore").strip()
                if not line.startswith("data:"):
                    continue
                data = line[5:].strip()
                if data == "[DONE]":
                    break
                try:
                    delta = json.loads(data)["choices"][0].get("delta", {}).get("content")
                except (json.JSONDecodeError, KeyError, IndexError):
                    continue
                if delta:
                    yield delta


class InProcessProvider(LLMProvider):
    """Template for running your fine-tune directly inside WinSpotlight.

    Example with llama-cpp-python:
        from llama_cpp import Llama
        self.llm = Llama(model_path=cfg["model"], n_ctx=8192)
        for chunk in self.llm.create_chat_completion(messages, stream=True):
            yield chunk["choices"][0]["delta"].get("content", "")
    """

    def stream(self, messages):
        raise NotImplementedError("Fill in InProcessProvider.stream with your model's inference code.")


PROVIDERS = {"openai_compat": OpenAICompatProvider, "in_process": InProcessProvider}


def make_provider(cfg):
    if not cfg.get("enabled"):
        return None
    return PROVIDERS[cfg.get("provider", "openai_compat")](cfg)


def build_messages(cfg, question, context):
    msgs = [{"role": "system", "content": cfg["system_prompt"]}]
    if context:
        question = f"Relevant files from my computer:\n\n{context}\n\n---\n\n{question}"
    msgs.append({"role": "user", "content": question})
    return msgs


try:  # Qt is only needed by the legacy PySide6 front end, not the WinUI backend
    from PySide6.QtCore import QThread, Signal
except ImportError:
    QThread = None

if QThread is not None:
    class LLMWorker(QThread):
        token = Signal(str)
        failed = Signal(str)

        def __init__(self, provider, messages):
            super().__init__()
            self.provider, self.messages = provider, messages
            self.cancelled = False

        def run(self):
            try:
                for t in self.provider.stream(self.messages):
                    if self.cancelled:
                        return
                    self.token.emit(t)
            except Exception as e:
                self.failed.emit(str(e))
