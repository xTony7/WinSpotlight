"""English or Spanish for text the backend sends to the UI.

Follows the Windows display language unless config.json sets "language" to "en" or "es";
the WinUI front end makes the same choice.
"""
import ctypes

from . import config


def _spanish():
    lang = config.load().get("language", "auto")
    if lang in ("en", "es"):
        return lang == "es"
    try:
        return ctypes.windll.kernel32.GetUserDefaultUILanguage() & 0x3FF == 0x0A  # LANG_SPANISH
    except (AttributeError, OSError):
        return False


ES = _spanish()


def t(en, es):
    return es if ES else en
