#!/usr/bin/env python3
# The agent's tray icon on Linux, as on Windows: it shows that the agent is running (or why it is
# not), opens the live log, and quits the agent. GTK 3 with AppIndicator (StatusNotifierItem), which
# KDE, Ubuntu's GNOME, Cinnamon, MATE and XFCE show; a legacy system tray when there is no
# AppIndicator library.
#
# The agent (TrayIcon.cs) starts it with python3 and they talk over its stdin and stdout, one line
# per message, as on macOS:
#
#   agent → tray   "state running <text>"   the server is up; <text> goes in the menu
#                  "state error <text>"     it is not; the icon becomes a warning sign
#   tray → agent   "ready"                  the icon is up
#                  "quit"                   stop the agent (Quit in the menu)
#                  "warn <text>"            something worth a line in the agent's log
#                  "fail <text>"            no icon can be shown; this program exits
#
# When the agent exits, stdin closes and this program exits with it.
#
#   python3 PazScanTray.py --version <version> --logs <log folder>

import importlib
import os
import signal
import sys
import threading
import warnings
from datetime import datetime

HERE = os.path.dirname(os.path.realpath(__file__))
ICON_NAME = "paz-scan-agent"  # paz-scan-agent.png beside this file
ICON_FILE = os.path.join(HERE, ICON_NAME + ".png")


def tell(message):
    try:
        sys.stdout.buffer.write((message.replace("\n", " ") + "\n").encode("utf-8"))
        sys.stdout.buffer.flush()
    except (BrokenPipeError, OSError):
        pass  # the agent has gone


def option(name, default=""):
    arguments = sys.argv[1:]
    if name in arguments:
        index = arguments.index(name)
        if index + 1 < len(arguments):
            return arguments[index + 1]
    return default


try:
    # ps and pkill show "PazScanTray" rather than "python3".
    import ctypes

    ctypes.CDLL(None).prctl(15, b"PazScanTray", 0, 0, 0)  # PR_SET_NAME
except (OSError, AttributeError):
    pass

try:
    import gi

    gi.require_version("GLib", "2.0")
    from gi.repository import GLib

    # The log window's app id: desktops match it to paz-scan-agent.desktop for its icon.
    GLib.set_prgname(ICON_NAME)
    GLib.set_application_name("Paz Scan Agent")
    gi.require_version("Gdk", "3.0")
    gi.require_version("Gtk", "3.0")
    from gi.repository import Gdk, Gio, Gtk, Pango
except (ImportError, ValueError) as error:
    tell("fail it needs GTK 3 for Python (Debian/Ubuntu: sudo apt install python3-gi gir1.2-gtk-3.0): %s" % error)
    sys.exit(3)

if Gdk.Display.get_default() is None:
    tell("fail there is no display to show it on")
    sys.exit(3)

AppIndicator = None
for _name in ("AyatanaAppIndicator3", "AppIndicator3"):
    try:
        gi.require_version(_name, "0.1")
        AppIndicator = importlib.import_module("gi.repository." + _name)
        break
    except (ImportError, ValueError):
        pass


class LogWindow(Gtk.Window):
    """Tails the newest log file in the agent's log folder, as "View live logs" does on Windows.
    Warnings show in orange, errors in red, debug lines dimmed."""

    TAIL_BYTES = 256 * 1024
    MAX_CHARACTERS = 500_000

    def __init__(self, directory, on_close):
        super().__init__(title="Paz Scan Agent — Live Logs")
        self.directory = directory
        self.on_close = on_close
        self.file = None
        self.position = 0
        self.partial_line = b""  # the end of the last read when it stopped mid-line
        self.skipping_fragment = False  # after jumping into the middle of a large file
        self.level = None  # the last entry's, which its continuation lines (a stack trace) share

        self.set_default_size(1000, 650)
        if os.path.exists(ICON_FILE):
            self.set_icon_from_file(ICON_FILE)

        self.view = Gtk.TextView(editable=False, cursor_visible=False, monospace=True, wrap_mode=Gtk.WrapMode.NONE)
        for side in ("left", "right", "top", "bottom"):
            getattr(self.view, "set_%s_margin" % side)(6)
        self.text = self.view.get_buffer()
        self.text.create_tag("quiet", foreground="#8a8a8a")
        self.text.create_tag("warning", foreground="#e66100")
        self.text.create_tag("error", foreground="#e01b24")
        self.text.create_tag("note", foreground="#8a8a8a", style=Pango.Style.ITALIC)
        self.end = self.text.create_mark("end", self.text.get_end_iter(), False)
        scroll = Gtk.ScrolledWindow()
        scroll.add(self.view)

        self.pause = Gtk.CheckButton(label="Pause")
        self.follow = Gtk.CheckButton(label="Follow", active=True)
        self.follow.connect("toggled", lambda button: button.get_active() and self.scroll_to_end())
        self.search = Gtk.SearchEntry(placeholder_text="Find (Ctrl+F)", width_chars=24)
        self.search.connect("search-changed", lambda entry: self.find(forward=True, from_start_of_selection=True))
        self.search.connect("activate", lambda entry: self.find(forward=True))
        self.search.connect("next-match", lambda entry: self.find(forward=True))
        self.search.connect("previous-match", lambda entry: self.find(forward=False))

        bar = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=6, margin=8)
        for label, action in (("Open Folder", self.open_folder), ("Copy All", self.copy_all), ("Clear View", self.clear_view)):
            button = Gtk.Button(label=label)
            button.connect("clicked", lambda _, act=action: act())
            bar.pack_start(button, False, False, 0)
        bar.pack_start(self.pause, False, False, 12)
        bar.pack_start(self.follow, False, False, 0)
        bar.pack_end(self.search, False, False, 0)

        self.status = Gtk.Label(xalign=0, ellipsize=Pango.EllipsizeMode.MIDDLE, margin=6, margin_start=10)
        self.status.get_style_context().add_class("dim-label")

        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL)
        box.pack_start(bar, False, False, 0)
        box.pack_start(Gtk.Separator(), False, False, 0)
        box.pack_start(scroll, True, True, 0)
        box.pack_start(Gtk.Separator(), False, False, 0)
        box.pack_start(self.status, False, False, 0)
        self.add(box)

        self.connect("key-press-event", self.on_key)
        self.connect("destroy", self.on_destroy)
        self.timer = GLib.timeout_add(1000, self.tick)
        self.refresh()
        self.show_all()
        self.scroll_to_end()

    # Keys and buttons

    def on_key(self, _, event):
        control = event.state & Gdk.ModifierType.CONTROL_MASK
        if control and event.keyval in (Gdk.KEY_w, Gdk.KEY_W):
            self.destroy()
            return True
        if control and event.keyval in (Gdk.KEY_f, Gdk.KEY_F):
            self.search.grab_focus()
            return True
        return False

    def on_destroy(self, _):
        GLib.source_remove(self.timer)
        self.on_close()

    def open_folder(self):
        os.makedirs(self.directory, exist_ok=True)
        try:
            Gtk.show_uri_on_window(self, GLib.filename_to_uri(self.directory, None), Gdk.CURRENT_TIME)
        except GLib.Error as error:
            tell("warn could not open %s: %s" % (self.directory, error.message))

    def copy_all(self):
        start, end = self.text.get_bounds()
        clipboard = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD)
        clipboard.set_text(self.text.get_text(start, end, False), -1)
        clipboard.store()

    def clear_view(self):
        self.text.set_text("")

    def find(self, forward, from_start_of_selection=False):
        needle = self.search.get_text()
        if not needle:
            return
        flags = Gtk.TextSearchFlags.CASE_INSENSITIVE | Gtk.TextSearchFlags.TEXT_ONLY
        bounds = self.text.get_selection_bounds()
        if bounds:
            start = bounds[0] if forward and from_start_of_selection else bounds[1] if forward else bounds[0]
        else:
            start = self.text.get_start_iter() if forward else self.text.get_end_iter()
        found = start.forward_search(needle, flags, None) if forward else start.backward_search(needle, flags, None)
        if found is None:  # wrap around
            edge = self.text.get_start_iter() if forward else self.text.get_end_iter()
            found = edge.forward_search(needle, flags, None) if forward else edge.backward_search(needle, flags, None)
        if found is not None:
            self.text.select_range(found[0], found[1])
            self.view.scroll_to_iter(found[0], 0.1, False, 0, 0)

    # Tailing

    def tick(self):
        self.refresh()
        return True

    def refresh(self):
        if self.pause.get_active():
            return
        newest = self.newest_log()
        if newest is None:
            self.status.set_text("No log file yet in %s" % self.directory)
            return
        name = os.path.basename(newest)

        if newest != self.file:
            # A new day, or the last file reached its size limit.
            if self.file is not None:
                self.note("── continued in %s ──" % name)
            self.file = newest
            self.position = 0
            self.partial_line = b""
            self.skipping_fragment = False

        try:
            with open(newest, "rb") as log:
                length = log.seek(0, os.SEEK_END)
                if length < self.position:
                    # Replaced or cut short: read it again from the top.
                    self.note("── %s started over ──" % name)
                    self.position = 0
                    self.partial_line = b""
                    self.skipping_fragment = False
                if self.position == 0 and length > self.TAIL_BYTES:
                    self.position = length - self.TAIL_BYTES
                    self.skipping_fragment = True
                    self.note("… earlier lines omitted …")
                log.seek(self.position)
                data = log.read()
            self.position += len(data)
            self.consume(data)
            updated = datetime.fromtimestamp(os.stat(newest).st_mtime).strftime("%X")
            self.status.set_text("%s · %s · updated %s" % (name, size_text(self.position), updated))
        except OSError:
            self.status.set_text("%s · waiting for the agent to write" % name)

    def newest_log(self):
        """The agent writes one file a day and starts another at 5 MB, so the one written last is current."""
        newest = None
        try:
            with os.scandir(self.directory) as entries:
                for entry in entries:
                    if not (entry.name.startswith("agent-") and entry.name.endswith(".log")):
                        continue
                    try:
                        key = (entry.stat().st_mtime_ns, entry.name)
                    except OSError:
                        continue
                    if newest is None or key > newest[0]:
                        newest = (key, entry.path)
        except OSError:
            return None
        return newest[1] if newest else None

    def consume(self, data):
        """Shows the complete lines read so far. A newline byte never occurs inside a UTF-8
        character, so splitting there never splits a character."""
        data = self.partial_line + data
        if self.skipping_fragment:
            newline = data.find(b"\n")
            if newline < 0:
                self.partial_line = b""
                return
            data = data[newline + 1:]
            self.skipping_fragment = False
        last = data.rfind(b"\n")
        if last < 0:
            self.partial_line = data
            return
        self.partial_line = data[last + 1:]

        runs = []  # consecutive lines of one colour, inserted together
        for line in data[:last].decode("utf-8", "replace").split("\n"):
            level = level_of(line)
            if level is not False:
                self.level = level
            if runs and runs[-1][0] == self.level:
                runs[-1][1].append(line)
            else:
                runs.append((self.level, [line]))
        for tag, lines in runs:
            self.append("\n".join(lines) + "\n", tag)

    def note(self, text):
        self.append(text + "\n", "note")

    def append(self, text, tag):
        if tag:
            self.text.insert_with_tags_by_name(self.text.get_end_iter(), text, tag)
        else:
            self.text.insert(self.text.get_end_iter(), text)
        excess = self.text.get_char_count() - self.MAX_CHARACTERS
        if excess > 0:
            # Drop whole lines from the top.
            cut = self.text.get_iter_at_offset(excess)
            if not cut.starts_line():
                cut.forward_line()
            self.text.delete(self.text.get_start_iter(), cut)
        if self.follow.get_active():
            self.scroll_to_end()

    def scroll_to_end(self):
        self.view.scroll_to_mark(self.end, 0, False, 0, 1)


def level_of(line):
    """The colour of a line, from the level column the agent writes:
    "2026-10-25 14:03:22.123 Information PazScan.Agent: message". False for a line that does not
    start an entry, such as an exception's stack trace."""
    if len(line) <= 24 or line[4] != "-" or line[10] != " " or line[13] != ":" or line[23] != " ":
        return False
    name = line[24:36].split(" ", 1)[0]
    return {"Trace": "quiet", "Debug": "quiet", "Information": None, "Warning": "warning",
            "Error": "error", "Critical": "error"}.get(name, False)


def size_text(size):
    for unit in ("bytes", "KB", "MB"):
        if size < 1000 or unit == "MB":
            return "%d %s" % (size, unit) if unit == "bytes" else "%.1f %s" % (size, unit)
        size /= 1000


class Tray:
    def __init__(self, version, log_directory):
        self.log_directory = log_directory
        self.log_window = None
        self.tray_host = None

        self.menu = Gtk.Menu()
        title = Gtk.MenuItem(label="Paz Scan Agent %s" % version, sensitive=False)
        self.state = Gtk.MenuItem(label="Starting…", sensitive=False)
        logs = Gtk.MenuItem(label="View Live Logs…")
        logs.connect("activate", lambda _: self.show_logs())
        folder = Gtk.MenuItem(label="Open Logs Folder")
        folder.connect("activate", lambda _: self.open_logs_folder())
        quit_item = Gtk.MenuItem(label="Quit Paz Scan Agent")
        quit_item.connect("activate", lambda _: self.quit())
        for item in (title, self.state, Gtk.SeparatorMenuItem(), logs, folder, Gtk.SeparatorMenuItem(), quit_item):
            self.menu.append(item)
        self.menu.show_all()

        if AppIndicator is not None:
            self.indicator = AppIndicator.Indicator.new(
                ICON_NAME, ICON_NAME, AppIndicator.IndicatorCategory.APPLICATION_STATUS)
            self.indicator.set_icon_theme_path(HERE)
            self.indicator.set_title("Paz Scan Agent")
            self.indicator.set_menu(self.menu)
            self.indicator.set_secondary_activate_target(logs)  # middle click
            self.indicator.set_status(AppIndicator.IndicatorStatus.ACTIVE)
            self.status_icon = None
            # Whether anything shows AppIndicator icons; plain GNOME needs an extension for that.
            Gio.bus_watch_name(Gio.BusType.SESSION, "org.kde.StatusNotifierWatcher", Gio.BusNameWatcherFlags.NONE,
                               lambda *_: setattr(self, "tray_host", True), lambda *_: setattr(self, "tray_host", False))
            GLib.timeout_add_seconds(20, self.check_tray_host)
        else:
            # A legacy (XEmbed) system tray, such as XFCE's or MATE's without their AppIndicator plugin.
            tell("warn no AppIndicator library (Debian/Ubuntu: gir1.2-ayatanaappindicator3-0.1); "
                 "using the legacy system tray, which many desktops no longer show")
            with warnings.catch_warnings():
                warnings.simplefilter("ignore")
                self.status_icon = Gtk.StatusIcon()
            self.status_icon.set_title("Paz Scan Agent")
            self.status_icon.connect("activate", lambda icon: self.menu.popup_at_pointer(None))
            self.status_icon.connect("popup-menu", lambda icon, button, time: self.menu.popup_at_pointer(None))
            self.indicator = None
        self.show("starting", "Starting…")

    def check_tray_host(self):
        if self.tray_host is False:
            tell("warn the desktop shows no AppIndicator icons (no StatusNotifier host), so the icon "
                 "appears only if there is a legacy system tray. On GNOME, the AppIndicator extension "
                 "adds one: gnome-shell-extension-appindicator")
        return False

    def show(self, state, text):
        icon = "dialog-warning" if state == "error" else ICON_NAME
        if self.indicator is not None:
            self.indicator.set_icon_full(icon, "Paz Scan Agent — %s" % text)
        else:
            if state == "error":
                self.status_icon.set_from_icon_name(icon)
            elif os.path.exists(ICON_FILE):
                self.status_icon.set_from_file(ICON_FILE)
            self.status_icon.set_tooltip_text("Paz Scan Agent — %s" % text)
        self.state.set_label(text if len(text) <= 100 else text[:99] + "…")

    def handle(self, line):
        parts = line.split(" ", 2)
        if len(parts) >= 2 and parts[0] == "state" and parts[1] in ("running", "error"):
            self.show(parts[1], parts[2] if len(parts) > 2 else "")
        return False

    def show_logs(self):
        if self.log_window is None:
            self.log_window = LogWindow(self.log_directory, self.forget_log_window)
        self.log_window.present_with_time(Gtk.get_current_event_time())

    def forget_log_window(self):
        self.log_window = None

    def open_logs_folder(self):
        os.makedirs(self.log_directory, exist_ok=True)
        try:
            Gtk.show_uri_on_window(None, GLib.filename_to_uri(self.log_directory, None), Gdk.CURRENT_TIME)
        except GLib.Error as error:
            tell("warn could not open %s: %s" % (self.log_directory, error.message))

    def quit(self):
        tell("quit")
        Gtk.main_quit()


def listen_to_agent(tray):
    while True:
        line = sys.stdin.buffer.readline()
        if not line:
            break
        GLib.idle_add(tray.handle, line.decode("utf-8", "replace").rstrip("\r\n"))
    # The agent has gone; it no longer needs an icon.
    GLib.idle_add(Gtk.main_quit)


def main():
    log_directory = option("--logs", os.path.join(
        os.environ.get("XDG_DATA_HOME") or os.path.expanduser("~/.local/share"), "Paz Scan Agent", "logs"))
    tray = Tray(option("--version"), log_directory)
    for number in (signal.SIGTERM, signal.SIGINT, signal.SIGHUP):
        GLib.unix_signal_add(GLib.PRIORITY_DEFAULT, number, lambda: Gtk.main_quit() or False)
    threading.Thread(target=listen_to_agent, args=(tray,), daemon=True).start()
    tell("ready")
    Gtk.main()


if __name__ == "__main__":
    main()
