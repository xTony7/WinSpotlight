"""Spotlight-style floating search panel."""
import ctypes
import datetime as dt
import os
import subprocess
import winreg

from PySide6.QtCore import QEvent, QFileInfo, QRect, QSize, Qt, QTimer, Signal
from PySide6.QtGui import (QColor, QCursor, QFont, QFontMetrics, QGuiApplication, QIcon, QPainter,
                           QPainterPath, QPen, QPixmap)
from PySide6.QtWidgets import (QFileIconProvider, QFrame, QGraphicsDropShadowEffect, QHBoxLayout, QLabel,
                               QLineEdit, QListWidget, QListWidgetItem, QStackedWidget, QStyle,
                               QStyledItemDelegate, QTextBrowser, QVBoxLayout, QWidget)

from .llm import LLMWorker, build_messages

ROLE = Qt.UserRole + 1
WIDTH, BAR_H, BODY_H = 760, 60, 400


def system_dark():
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                            r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize") as k:
            return winreg.QueryValueEx(k, "AppsUseLightTheme")[0] == 0
    except OSError:
        return True


DARK = dict(bg=QColor(32, 32, 36, 238), border=QColor(255, 255, 255, 38), text="#f2f2f7", sub="#9a9aa2",
            sel=QColor(10, 132, 255), sel_text="#ffffff", line="rgba(255,255,255,0.09)", header="#8e8e93")
LIGHT = dict(bg=QColor(246, 246, 248, 240), border=QColor(0, 0, 0, 30), text="#1c1c1e", sub="#6e6e73",
             sel=QColor(10, 122, 255), sel_text="#ffffff", line="rgba(0,0,0,0.08)", header="#6e6e73")


def magnifier_pixmap(size, color):
    pm = QPixmap(size, size)
    pm.fill(Qt.transparent)
    p = QPainter(pm)
    p.setRenderHint(QPainter.Antialiasing)
    pen = QPen(QColor(color), size * 0.11)
    pen.setCapStyle(Qt.RoundCap)
    p.setPen(pen)
    r = size * 0.36
    p.drawEllipse(int(size * 0.1), int(size * 0.1), int(r * 2), int(r * 2))
    c = size * 0.1 + r * 2 * 0.85
    p.drawLine(int(c), int(c), int(size * 0.9), int(size * 0.9))
    p.end()
    return pm


def sparkle_icon(size, color):
    pm = QPixmap(size, size)
    pm.fill(Qt.transparent)
    p = QPainter(pm)
    p.setRenderHint(QPainter.Antialiasing)
    p.setPen(Qt.NoPen)
    p.setBrush(QColor(color))
    path = QPainterPath()
    s, m = size, size / 2
    path.moveTo(m, s * 0.05)
    path.quadTo(m, m, s * 0.95, m)
    path.quadTo(m, m, m, s * 0.95)
    path.quadTo(m, m, s * 0.05, m)
    path.quadTo(m, m, m, s * 0.05)
    p.drawPath(path)
    p.end()
    return QIcon(pm)


def human_size(n):
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if n < 1024:
            return f"{n:.0f} {unit}" if unit == "B" else f"{n:.1f} {unit}"
        n /= 1024
    return f"{n:.1f} PB"


class ResultDelegate(QStyledItemDelegate):
    def __init__(self, theme, parent=None):
        super().__init__(parent)
        self.t = theme

    def sizeHint(self, opt, idx):
        d = idx.data(ROLE)
        return QSize(0, 26 if d.get("header") else 40)

    def paint(self, p, opt, idx):
        d = idx.data(ROLE)
        p.save()
        p.setRenderHint(QPainter.Antialiasing)
        r = opt.rect
        if d.get("header"):
            f = QFont(opt.font)
            f.setPointSizeF(8.5)
            f.setBold(True)
            p.setFont(f)
            p.setPen(QColor(self.t["header"]))
            p.drawText(r.adjusted(14, 6, -8, 0), Qt.AlignLeft | Qt.AlignVCenter, d["title"].upper())
            p.restore()
            return
        selected = opt.state & QStyle.State_Selected
        if selected:
            path = QPainterPath()
            path.addRoundedRect(r.adjusted(6, 1, -6, -1), 7, 7)
            p.fillPath(path, self.t["sel"])
        icon = idx.data(Qt.DecorationRole)
        if icon:
            icon.paint(p, QRect(r.left() + 14, r.top() + 6, 28, 28))
        tc = QColor(self.t["sel_text"] if selected else self.t["text"])
        sc = QColor(255, 255, 255, 190) if selected else QColor(self.t["sub"])
        f = QFont(opt.font)
        f.setPointSizeF(10)
        p.setFont(f)
        fm = QFontMetrics(f)
        text_rect = r.adjusted(52, 3, -12, 0)
        has_sub = bool(d.get("subtitle"))
        p.setPen(tc)
        p.drawText(text_rect.adjusted(0, 0, 0, -18 if has_sub else -3), Qt.AlignLeft | Qt.AlignVCenter,
                   fm.elidedText(d["title"], Qt.ElideRight, text_rect.width()))
        if has_sub:
            f2 = QFont(opt.font)
            f2.setPointSizeF(8)
            p.setFont(f2)
            p.setPen(sc)
            p.drawText(text_rect.adjusted(0, 19, 0, -3), Qt.AlignLeft | Qt.AlignVCenter,
                       QFontMetrics(f2).elidedText(d["subtitle"], Qt.ElideMiddle, text_rect.width()))
        p.restore()


class Panel(QFrame):
    """Rounded translucent background with hairline border."""

    def __init__(self, theme):
        super().__init__()
        self.t = theme

    def paintEvent(self, _):
        p = QPainter(self)
        p.setRenderHint(QPainter.Antialiasing)
        path = QPainterPath()
        path.addRoundedRect(self.rect().adjusted(0, 0, -1, -1), 14, 14)
        p.fillPath(path, self.t["bg"])
        p.setPen(QPen(self.t["border"], 1))
        p.drawPath(path)


class SpotlightWindow(QWidget):
    request_reindex = Signal()

    def __init__(self, engine, llm_provider, llm_cfg):
        super().__init__(None, Qt.FramelessWindowHint | Qt.Tool | Qt.WindowStaysOnTopHint)
        self.engine, self.llm, self.llm_cfg = engine, llm_provider, llm_cfg
        self.t = DARK if system_dark() else LIGHT
        self.icons = QFileIconProvider()
        self.icon_cache = {}
        self.worker = None
        self.ai_text = ""
        self.results = []
        self.setAttribute(Qt.WA_TranslucentBackground)
        self.setWindowTitle("WinSpotlight")

        outer = QVBoxLayout(self)
        outer.setContentsMargins(30, 20, 30, 40)  # room for the drop shadow
        self.panel = Panel(self.t)
        shadow = QGraphicsDropShadowEffect(blurRadius=48, xOffset=0, yOffset=14)
        shadow.setColor(QColor(0, 0, 0, 120))
        self.panel.setGraphicsEffect(shadow)
        outer.addWidget(self.panel)

        v = QVBoxLayout(self.panel)
        v.setContentsMargins(0, 0, 0, 0)
        v.setSpacing(0)

        bar = QWidget()
        bar.setFixedHeight(BAR_H)
        h = QHBoxLayout(bar)
        h.setContentsMargins(18, 0, 18, 0)
        h.setSpacing(12)
        glass = QLabel()
        glass.setPixmap(magnifier_pixmap(22, self.t["sub"]))
        h.addWidget(glass)
        self.input = QLineEdit()
        self.input.setPlaceholderText("Spotlight Search")
        self.input.setFrame(False)
        f = QFont("Segoe UI Variable Display", 17)
        f.setWeight(QFont.Light)
        self.input.setFont(f)
        self.input.setStyleSheet(f"background: transparent; color: {self.t['text']};"
                                 f"selection-background-color: {self.t['sel'].name()};")
        self.input.textChanged.connect(self._schedule_search)
        self.input.installEventFilter(self)
        h.addWidget(self.input, 1)
        v.addWidget(bar)

        self.body = QWidget()
        self.body.setFixedHeight(BODY_H)
        bv = QVBoxLayout(self.body)
        bv.setContentsMargins(0, 0, 0, 0)
        bv.setSpacing(0)
        bv.addWidget(self._hline())
        row = QHBoxLayout()
        row.setSpacing(0)
        row.setContentsMargins(0, 0, 0, 0)

        self.list = QListWidget()
        self.list.setFixedWidth(300)
        self.list.setFrameShape(QFrame.NoFrame)
        self.list.setFocusPolicy(Qt.NoFocus)
        self.list.setItemDelegate(ResultDelegate(self.t, self.list))
        self.list.setVerticalScrollBarPolicy(Qt.ScrollBarAlwaysOff)
        self.list.setStyleSheet("QListWidget{background:transparent; padding-top:4px;}")
        self.list.currentRowChanged.connect(self._update_preview)
        self.list.itemDoubleClicked.connect(lambda *_: self.activate())
        row.addWidget(self.list)
        vline = QFrame()
        vline.setFixedWidth(1)
        vline.setStyleSheet(f"background:{self.t['line']};")
        row.addWidget(vline)

        self.stack = QStackedWidget()
        self.preview = self._build_preview()
        self.ai_view = QTextBrowser()
        self.ai_view.setOpenExternalLinks(True)
        self.ai_view.setFrameShape(QFrame.NoFrame)
        self.ai_view.setStyleSheet(f"background:transparent; color:{self.t['text']}; padding:14px;"
                                   "font-size:10.5pt;")
        self.stack.addWidget(self.preview)
        self.stack.addWidget(self.ai_view)
        row.addWidget(self.stack, 1)
        bv.addLayout(row, 1)
        bv.addWidget(self._hline())

        foot = QHBoxLayout()
        foot.setContentsMargins(16, 6, 16, 7)
        self.status = QLabel("")
        hints = "↵ Open    Ctrl+↵ Show in Explorer"
        if self.llm:
            hints += f"    Tab Ask {self.llm.name}"
        self.hints = QLabel(hints)
        for lbl in (self.status, self.hints):
            lbl.setStyleSheet(f"color:{self.t['sub']}; font-size:8pt;")
        foot.addWidget(self.status, 1)
        foot.addWidget(self.hints)
        bv.addLayout(foot)
        v.addWidget(self.body)
        self.body.hide()

        self.timer = QTimer(self, singleShot=True, interval=110)
        self.timer.timeout.connect(self.run_search)
        self._resize()

    # ---- construction helpers -------------------------------------------
    def _hline(self):
        line = QFrame()
        line.setFixedHeight(1)
        line.setStyleSheet(f"background:{self.t['line']};")
        return line

    def _build_preview(self):
        w = QWidget()
        lay = QVBoxLayout(w)
        lay.setContentsMargins(20, 22, 20, 14)
        lay.setSpacing(6)
        self.pv_icon = QLabel(alignment=Qt.AlignCenter)
        self.pv_name = QLabel(alignment=Qt.AlignCenter, wordWrap=True)
        self.pv_name.setStyleSheet(f"color:{self.t['text']}; font-size:13pt; font-weight:600;")
        self.pv_kind = QLabel(alignment=Qt.AlignCenter)
        self.pv_kind.setStyleSheet(f"color:{self.t['sub']}; font-size:9pt;")
        self.pv_snip = QLabel(wordWrap=True, alignment=Qt.AlignTop | Qt.AlignLeft)
        self.pv_snip.setStyleSheet(f"color:{self.t['text']}; font-size:9pt; padding-top:8px;")
        self.pv_meta = QLabel(wordWrap=True, alignment=Qt.AlignTop | Qt.AlignLeft)
        self.pv_meta.setTextFormat(Qt.RichText)
        self.pv_meta.setStyleSheet(f"color:{self.t['sub']}; font-size:8.5pt;")
        for x in (self.pv_icon, self.pv_name, self.pv_kind, self.pv_snip):
            lay.addWidget(x)
        lay.addStretch(1)
        lay.addWidget(self._hline())
        lay.addWidget(self.pv_meta)
        return w

    def _resize(self):
        m = self.layout().contentsMargins()
        h = BAR_H + (0 if self.body.isHidden() else BODY_H) + m.top() + m.bottom()
        self.setFixedSize(WIDTH + m.left() + m.right(), h)

    def icon_for(self, r):
        key = r["path"] if r["kind"] in ("app", "folder") or r["path"].lower().endswith(".exe") \
            else os.path.splitext(r["path"])[1].lower()
        if key not in self.icon_cache:
            self.icon_cache[key] = self.icons.icon(QFileInfo(r["path"]))
        return self.icon_cache[key]

    # ---- show / hide ----------------------------------------------------
    def toggle(self):
        if self.isVisible():
            self.hide()
            return
        screen = QGuiApplication.screenAt(QCursor.pos()) or QGuiApplication.primaryScreen()
        g = screen.availableGeometry()
        self.move(g.center().x() - self.width() // 2, g.top() + int(g.height() * 0.2))
        self.show()
        self.raise_()
        self.activateWindow()
        ctypes.windll.user32.SetForegroundWindow(int(self.winId()))
        self.input.setFocus()
        self.input.selectAll()

    def event(self, e):
        if e.type() == QEvent.WindowDeactivate:
            self.hide()
        return super().event(e)

    def set_status(self, text):
        self.status.setText(text)

    # ---- searching -------------------------------------------------------
    def _schedule_search(self):
        self._cancel_ai()
        self.timer.start()

    def run_search(self):
        q = self.input.text().strip()
        self.list.clear()
        self.results = []
        if not q:
            self.body.hide()
            self._resize()
            return
        top, sections, calc = self.engine.search(q)

        def add(d, icon=None):
            it = QListWidgetItem()
            it.setData(ROLE, d)
            if icon:
                it.setData(Qt.DecorationRole, icon)
            if d.get("header"):
                it.setFlags(Qt.NoItemFlags)
            self.list.addItem(it)

        if calc is not None:
            add({"header": True, "title": "Calculator"})
            add({"type": "calc", "title": f"= {calc}", "subtitle": q, "value": calc},
                QIcon(magnifier_pixmap(28, self.t["sub"])))
        if top:
            add({"header": True, "title": "Top Hit"})
            add(self._item(top), self.icon_for(top))
        if self.llm:
            add({"header": True, "title": self.llm.name})
            add({"type": "ask", "title": f"Ask “{q}”", "subtitle": "Answer using local model + your files"},
                sparkle_icon(28, "#bf5af2"))
        for title, items in sections:
            add({"header": True, "title": title})
            for r in items:
                add(self._item(r), self.icon_for(r))
        for i in range(self.list.count()):
            if self.list.item(i).flags() & Qt.ItemIsSelectable:
                self.list.setCurrentRow(i)
                break
        self.body.setVisible(self.list.count() > 0)
        self._resize()

    def _item(self, r):
        self.results.append(r)
        parent = os.path.dirname(r["path"])
        sub = "Application" if r["kind"] == "app" else parent.replace(os.path.expanduser("~"), "~")
        return {"type": "file", "title": r["name"].removesuffix(".lnk"), "subtitle": sub, "r": r}

    def _current(self):
        it = self.list.currentItem()
        return it.data(ROLE) if it else None

    def _update_preview(self, _row):
        d = self._current()
        if not d:
            return
        if d["type"] == "ask":
            self.stack.setCurrentWidget(self.ai_view)
            if not self.worker:
                self.ai_view.setMarkdown(f"*Press Tab or ↵ to ask {self.llm.name}.*")
            return
        self.stack.setCurrentWidget(self.preview)
        if d["type"] == "calc":
            self.pv_icon.setPixmap(magnifier_pixmap(64, self.t["sub"]))
            self.pv_name.setText(d["value"])
            self.pv_kind.setText("Calculator · ↵ copies result")
            self.pv_snip.setText("")
            self.pv_meta.setText(d["subtitle"])
            return
        r = d["r"]
        self.pv_icon.setPixmap(self.icon_for(r).pixmap(72, 72))
        self.pv_name.setText(d["title"])
        kind = {"app": "Application", "folder": "Folder"}.get(r["kind"], r["kind"].title())
        size = "" if r["kind"] in ("folder", "app") else f" · {human_size(r['size'] or 0)}"
        self.pv_kind.setText(kind + size)
        snip = r.get("snippet") or ""
        self.pv_snip.setText((snip[:360] + "…") if len(snip) > 360 else snip)
        mod = dt.datetime.fromtimestamp(r["mtime"] or 0).strftime("%b %d, %Y at %I:%M %p")
        self.pv_meta.setText(f"<b>Modified</b>&nbsp; {mod}<br><b>Where</b>&nbsp; {os.path.dirname(r['path'])}")

    # ---- actions ---------------------------------------------------------
    def eventFilter(self, obj, e):
        if obj is self.input and e.type() == QEvent.KeyPress:
            k, mods = e.key(), e.modifiers()
            if k == Qt.Key_Escape:
                if self.input.text():
                    self.input.clear()
                else:
                    self.hide()
                return True
            if k in (Qt.Key_Down, Qt.Key_Up):
                self._move(1 if k == Qt.Key_Down else -1)
                return True
            if k in (Qt.Key_Return, Qt.Key_Enter):
                if self.timer.isActive():
                    self.timer.stop()
                    self.run_search()
                self.activate(reveal=bool(mods & Qt.ControlModifier))
                return True
            if k == Qt.Key_Tab and self.llm and self.input.text().strip():
                self.ask_ai()
                return True
        return super().eventFilter(obj, e)

    def _move(self, step):
        row = self.list.currentRow()
        n = self.list.count()
        while 0 <= row + step < n:
            row += step
            if self.list.item(row).flags() & Qt.ItemIsSelectable:
                self.list.setCurrentRow(row)
                return

    def activate(self, reveal=False):
        d = self._current()
        if not d:
            return
        if d["type"] == "ask":
            self.ask_ai()
            return
        if d["type"] == "calc":
            QGuiApplication.clipboard().setText(d["value"].replace(",", ""))
            self.hide()
            return
        path = d["r"]["path"]
        try:
            if reveal:
                subprocess.Popen(["explorer", "/select,", path])
            else:
                os.startfile(path)
        except OSError as e:
            self.set_status(f"Could not open: {e}")
            return
        self.hide()

    # ---- LLM -------------------------------------------------------------
    def ask_ai(self):
        q = self.input.text().strip()
        if not q or not self.llm:
            return
        if self.timer.isActive():  # flush a pending search so it can't replace the answer view
            self.timer.stop()
            self.run_search()
        self._cancel_ai()
        for i in range(self.list.count()):
            d = self.list.item(i).data(ROLE)
            if d.get("type") == "ask":
                self.list.setCurrentRow(i)
        self.stack.setCurrentWidget(self.ai_view)
        ctx = self.engine.context_for(self.results[:5]) if self.llm_cfg["use_search_context"] else ""
        self.ai_text = ""
        self.ai_view.setMarkdown("*Thinking…*")
        self.worker = LLMWorker(self.llm, build_messages(self.llm_cfg, q, ctx))
        self.worker.token.connect(self._on_token)
        self.worker.failed.connect(lambda m: self.ai_view.setMarkdown(
            f"**Couldn't reach {self.llm.name}**\n\n`{m}`\n\nCheck `llm.base_url` in config.json."))
        self.worker.start()

    def _on_token(self, t):
        self.ai_text += t
        self.ai_view.setMarkdown(self.ai_text)
        sb = self.ai_view.verticalScrollBar()
        sb.setValue(sb.maximum())

    def _cancel_ai(self):
        if self.worker:
            self.worker.cancelled = True
            self.worker.token.disconnect()
            self.worker = None
