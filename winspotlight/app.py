"""Entry point: tray icon, global hotkey, wiring."""
import ctypes
import ctypes.wintypes as wt
import os
import sys

from PySide6.QtCore import QObject, QThread, Signal
from PySide6.QtGui import QIcon
from PySide6.QtWidgets import QApplication, QMenu, QSystemTrayIcon

from . import config, store
from .embeddings import Embedder
from .indexer import Indexer
from .llm import make_provider
from .search import SearchEngine
from .ui import SpotlightWindow, magnifier_pixmap

WM_HOTKEY, WM_QUIT = 0x0312, 0x0012
MODS = {"alt": 0x1, "ctrl": 0x2, "control": 0x2, "shift": 0x4, "win": 0x8}
MOD_NOREPEAT = 0x4000
NAMED_VK = {"space": 0x20, "enter": 0x0D, "tab": 0x09, "esc": 0x1B, **{f"f{i}": 0x6F + i for i in range(1, 13)}}


def parse_hotkey(spec):
    mods, vk = MOD_NOREPEAT, None
    for part in spec.lower().replace(" ", "").split("+"):
        if part in MODS:
            mods |= MODS[part]
        elif part in NAMED_VK:
            vk = NAMED_VK[part]
        elif len(part) == 1:
            vk = ord(part.upper())
    if vk is None:
        raise ValueError(f"Bad hotkey: {spec}")
    return mods, vk


class StatusBridge(QObject):
    """Marshals indexer status (from a plain thread) onto the Qt main thread."""
    status = Signal(str)


class HotkeyThread(QThread):
    pressed = Signal()
    failed = Signal(str)

    def __init__(self, spec):
        super().__init__()
        self.spec = spec
        self.tid = None

    def run(self):
        user32 = ctypes.windll.user32
        self.tid = ctypes.windll.kernel32.GetCurrentThreadId()
        mods, vk = parse_hotkey(self.spec)
        if not user32.RegisterHotKey(None, 1, mods, vk):
            self.failed.emit(f"Hotkey {self.spec} is taken by another app — change it in config.json")
            return
        msg = wt.MSG()
        while user32.GetMessageW(ctypes.byref(msg), None, 0, 0) > 0:
            if msg.message == WM_HOTKEY:
                self.pressed.emit()
        user32.UnregisterHotKey(None, 1)

    def stop(self):
        if self.tid:
            ctypes.windll.user32.PostThreadMessageW(self.tid, WM_QUIT, 0, 0)
        self.wait(1000)


def main():
    ctypes.windll.shcore.SetProcessDpiAwareness(2)
    ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID("WinSpotlight")
    app = QApplication(sys.argv)
    app.setQuitOnLastWindowClosed(False)
    cfg = config.load()

    embedder = Embedder(cfg["embedding"])
    vindex = store.VectorIndex()
    con = store.connect(config.DB_PATH)
    vindex.load(con)
    engine = SearchEngine(con, embedder, vindex)
    llm = make_provider(cfg["llm"])

    win = SpotlightWindow(engine, llm, cfg["llm"])
    bridge = StatusBridge()
    indexer = Indexer(cfg, embedder, vindex, bridge.status.emit)
    bridge.status.connect(win.set_status)

    icon = QIcon(magnifier_pixmap(64, "#0a84ff"))
    app.setWindowIcon(icon)
    tray = QSystemTrayIcon(icon)
    tray.setToolTip(f"WinSpotlight — {cfg['hotkey']}")
    menu = QMenu()
    menu.addAction("Search", win.toggle)
    menu.addAction("Rebuild index now", indexer.rescan_now)
    menu.addAction("Open config", lambda: os.startfile(config.CONFIG_PATH))
    menu.addSeparator()
    menu.addAction("Quit", app.quit)
    tray.setContextMenu(menu)
    tray.activated.connect(lambda r: win.toggle() if r == QSystemTrayIcon.Trigger else None)
    bridge.status.connect(lambda s: tray.setToolTip(f"WinSpotlight — {cfg['hotkey']}\n{s}"))
    tray.show()

    hotkey = HotkeyThread(cfg["hotkey"])
    hotkey.pressed.connect(win.toggle)
    hotkey.failed.connect(lambda m: tray.showMessage("WinSpotlight", m))
    hotkey.start()
    indexer.start()

    tray.showMessage("WinSpotlight", f"Press {cfg['hotkey'].title()} to search. Indexing in the background…")

    def shutdown():
        hotkey.stop()
        indexer.stop()
        indexer.join(3)

    app.aboutToQuit.connect(shutdown)
    if "--show" in sys.argv:
        win.toggle()
    sys.exit(app.exec())


if __name__ == "__main__":
    main()
