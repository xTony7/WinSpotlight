"""Task agents: "fix <file> it does <problem>" -> a fixer agent edits the file, a reviewer agent checks it.

Works with any OpenAI-compatible endpoint. Native tool calls are used when the model emits them;
otherwise the model can reply with  <tool>{"name": ..., "args": {...}}</tool>  blocks (handy for
fine-tunes trained without a tool-calling template).

Safety: tools only touch files inside the target file's folder (the "workspace"); every write
first saves  <file>.winspotlight.bak ; shell commands are disabled unless agent.allow_commands is true.
"""
import difflib
import json
import os
import re
import shutil
import subprocess
import urllib.request

from .i18n import t

MAX_STEPS = 16
MAX_READ = 60_000

TOOLS = [
    {"name": "read_file", "description": "Read a text file in the workspace.",
     "parameters": {"type": "object", "properties": {"path": {"type": "string"}}, "required": ["path"]}},
    {"name": "list_dir", "description": "List a directory in the workspace.",
     "parameters": {"type": "object", "properties": {"path": {"type": "string"}}, "required": ["path"]}},
    {"name": "replace_in_file", "description": "Replace an exact snippet of a file with new text. old must match exactly once.",
     "parameters": {"type": "object", "properties": {"path": {"type": "string"}, "old": {"type": "string"},
                                                     "new": {"type": "string"}}, "required": ["path", "old", "new"]}},
    {"name": "write_file", "description": "Overwrite a whole file (use for small files or new files).",
     "parameters": {"type": "object", "properties": {"path": {"type": "string"}, "content": {"type": "string"}},
                    "required": ["path", "content"]}},
    {"name": "run_command", "description": "Run a shell command in the workspace (tests, linters). 60s timeout.",
     "parameters": {"type": "object", "properties": {"command": {"type": "string"}}, "required": ["command"]}},
    {"name": "finish", "description": "Stop and report what you did (or, as reviewer, your verdict).",
     "parameters": {"type": "object", "properties": {"summary": {"type": "string"},
                                                     "approved": {"type": "boolean"}}, "required": ["summary"]}},
]

FIXER_PROMPT = """You are the Fixer agent inside WinSpotlight. Task from the user: {task}
Target file: {target}
Workspace (you may only touch files under it): {workspace}
Read the file, find the cause, and make the smallest correct edit with replace_in_file.
{commands}When done, call finish with a short summary of the change."""

REVIEWER_PROMPT = """You are the Reviewer agent inside WinSpotlight. The user asked: {task}
The Fixer changed these files (unified diff below). Check the change is correct, minimal, and actually
addresses the task. You may read files. Call finish with approved=true, or approved=false and a
specific explanation of what is still wrong.

{diff}"""

TEXT_PROTOCOL = """
If you cannot emit native tool calls, call a tool by replying with exactly:
<tool>{"name": "<tool name>", "args": {...}}</tool>
Available tools: """ + ", ".join(t["name"] for t in TOOLS)


class Workspace:
    def __init__(self, target, allow_commands):
        self.target = os.path.abspath(target)
        self.root = os.path.dirname(self.target)
        self.allow_commands = allow_commands
        self.originals = {}  # path -> original text, for the diff / review

    def resolve(self, p):
        full = os.path.abspath(os.path.join(self.root, p))
        if os.path.commonpath([full, self.root]) != self.root:
            raise PermissionError(f"{p} is outside the workspace {self.root}")
        return full

    def _remember(self, full):
        if full not in self.originals:
            self.originals[full] = open(full, encoding="utf-8", errors="replace").read() if os.path.exists(full) else ""
            if os.path.exists(full):
                shutil.copy2(full, full + ".winspotlight.bak")

    def call(self, name, args):
        if name == "read_file":
            return open(self.resolve(args["path"]), encoding="utf-8", errors="replace").read(MAX_READ)
        if name == "list_dir":
            d = self.resolve(args.get("path", "."))
            return "\n".join(sorted(e + ("/" if os.path.isdir(os.path.join(d, e)) else "") for e in os.listdir(d))[:300])
        if name == "replace_in_file":
            full = self.resolve(args["path"])
            text = open(full, encoding="utf-8", errors="replace").read()
            n = text.count(args["old"])
            if n != 1:
                return f"error: old text matched {n} times; it must match exactly once"
            self._remember(full)
            open(full, "w", encoding="utf-8", newline="").write(text.replace(args["old"], args["new"]))
            return "ok"
        if name == "write_file":
            full = self.resolve(args["path"])
            self._remember(full)
            open(full, "w", encoding="utf-8", newline="").write(args["content"])
            return "ok"
        if name == "run_command":
            if not self.allow_commands:
                return "error: commands are disabled (agent.allow_commands=false)"
            r = subprocess.run(args["command"], shell=True, cwd=self.root, capture_output=True, text=True, timeout=60)
            return f"exit {r.returncode}\n{(r.stdout + r.stderr)[-8000:]}"
        return f"error: unknown tool {name}"

    def diff(self):
        out = []
        for full, before in self.originals.items():
            after = open(full, encoding="utf-8", errors="replace").read() if os.path.exists(full) else ""
            rel = os.path.relpath(full, self.root)
            out += difflib.unified_diff(before.splitlines(True), after.splitlines(True), f"a/{rel}", f"b/{rel}")
        return "".join(out)


def _chat(cfg, messages):
    body = {"model": cfg["model"], "messages": messages, "temperature": 0.1, "max_tokens": cfg["max_tokens"],
            "tools": [{"type": "function", "function": t} for t in TOOLS]}
    headers = {"Content-Type": "application/json"}
    if cfg.get("api_key"):
        headers["Authorization"] = f"Bearer {cfg['api_key']}"
    req = urllib.request.Request(cfg["base_url"].rstrip("/") + "/chat/completions",
                                 data=json.dumps(body).encode(), headers=headers)
    with urllib.request.urlopen(req, timeout=600) as r:
        return json.loads(r.read())["choices"][0]["message"]


def _tool_calls(msg):
    """Normalise native tool calls and <tool>{...}</tool> text calls into [(id, name, args)]."""
    calls = []
    for i, tc in enumerate(msg.get("tool_calls") or []):
        try:
            args = json.loads(tc["function"].get("arguments") or "{}")
        except json.JSONDecodeError:
            args = {}
        calls.append((tc.get("id") or f"call{i}", tc["function"]["name"], args))
    if not calls:
        for i, m in enumerate(re.finditer(r"<tool>(.*?)</tool>", msg.get("content") or "", re.S)):
            try:
                d = json.loads(m.group(1))
                calls.append((f"text{i}", d["name"], d.get("args", {})))
            except (json.JSONDecodeError, KeyError):
                pass
    return calls


def run_agent(cfg, name, system, ws, emit):
    """Generic tool loop. Returns the finish() args."""
    messages = [{"role": "system", "content": system + TEXT_PROTOCOL},
                {"role": "user", "content": "Begin."}]
    for _ in range(MAX_STEPS):
        msg = _chat(cfg, messages)
        calls = _tool_calls(msg)
        thinking = msg.get("reasoning_content") or msg.get("reasoning")  # llama.cpp / Ollama naming
        if thinking:  # reasoning models (LLA) think before acting
            emit({"agent": name, "type": "thought", "text": thinking.strip()[-600:]})
        if msg.get("content") and not msg.get("tool_calls"):
            thought = re.sub(r"<tool>.*?</tool>", "", msg["content"], flags=re.S).strip()
            if thought:
                emit({"agent": name, "type": "thought", "text": thought})
        if not calls:
            return {"summary": msg.get("content") or "", "approved": True}
        native = bool(msg.get("tool_calls"))
        messages.append({"role": "assistant", "content": msg.get("content") or "",
                         **({"tool_calls": msg["tool_calls"]} if native else {})})
        for cid, tool, args in calls:
            if tool == "finish":
                return args
            emit({"agent": name, "type": "tool", "tool": tool,
                  "arg": args.get("path") or args.get("command") or ""})
            try:
                result = ws.call(tool, args)
            except Exception as e:
                result = f"error: {e}"
            if native:
                messages.append({"role": "tool", "tool_call_id": cid, "content": result})
            else:
                messages.append({"role": "user", "content": f"<result tool=\"{tool}\">\n{result}\n</result>"})
    return {"summary": "Stopped: step limit reached.", "approved": False}


def run_task(cfg, task, target, emit):
    """Fixer -> Reviewer, with one revision round if the reviewer rejects."""
    acfg = cfg.get("agent", {})
    ws = Workspace(target, acfg.get("allow_commands", False))
    commands = "You may run tests with run_command.\n" if ws.allow_commands else ""
    emit({"type": "start", "target": ws.target})
    feedback = ""
    for round_ in range(2):
        fixer = FIXER_PROMPT.format(task=task, target=ws.target, workspace=ws.root, commands=commands) + feedback
        result = run_agent(cfg, "Fixer", fixer, ws, emit)
        emit({"agent": "Fixer", "type": "summary", "text": result.get("summary", "")})
        diff = ws.diff()
        if not diff:
            emit({"type": "done", "ok": False, "text": t("No changes were made.", "No se hizo ningún cambio."), "diff": ""})
            return
        review = run_agent(cfg, "Reviewer", REVIEWER_PROMPT.format(task=task, diff=diff[:20000]), ws, emit)
        emit({"agent": "Reviewer", "type": "summary", "text": review.get("summary", "")})
        if review.get("approved", True):
            emit({"type": "done", "ok": True, "text": result.get("summary", ""), "diff": diff})
            return
        feedback = f"\n\nA reviewer rejected your previous attempt:\n{review.get('summary')}\nFix it."
    emit({"type": "done", "ok": False, "text": t("Reviewer still not satisfied; backups saved as .winspotlight.bak.",
                                                "El revisor sigue sin aprobarlo; las copias quedaron como .winspotlight.bak."),
          "diff": ws.diff()})
