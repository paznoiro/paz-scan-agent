// The agent's menu bar icon on macOS, as the tray icon is on Windows: it shows that the agent is
// running (or why it is not), opens the live log, and quits the agent.
//
// The agent (TrayIcon.cs) starts this program beside itself and they talk over its stdin and stdout,
// one line per message:
//
//   agent → menu bar   "state running <text>"   the server is up; <text> goes in the menu
//                      "state error <text>"     it is not; the icon becomes a warning sign
//   menu bar → agent   "ready"                  the icon is in the menu bar
//                      "quit"                   stop the agent: Quit in the menu, or macOS quit us
//
// When the agent exits, stdin closes and this program exits with it.
//
//   PazScanMenuBar --version <version> --logs <log folder>

import AppKit

// A write after the agent has gone fails instead of killing this process.
signal(SIGPIPE, SIG_IGN)

func option(_ name: String) -> String? {
    let arguments = CommandLine.arguments
    guard let index = arguments.firstIndex(of: name), index + 1 < arguments.count else { return nil }
    return arguments[index + 1]
}

func tell(agent message: String) {
    try? FileHandle.standardOutput.write(contentsOf: Data((message + "\n").utf8))
}

@MainActor
final class MenuBarApp: NSObject, NSApplicationDelegate {
    private enum State { case starting, running, error }

    private let version: String
    private let logDirectory: URL
    private var statusItem: NSStatusItem!
    private let stateItem = NSMenuItem()
    private var logWindow: LogWindowController?

    init(version: String, logDirectory: URL) {
        self.version = version
        self.logDirectory = logDirectory
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.mainMenu = Self.keyboardMenu()
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        statusItem.menu = makeMenu()
        show(.starting, "Starting…")
        listenToAgent()
        tell(agent: "ready")
    }

    // Any orderly quit stops the agent too: Quit in the menu, Activity Monitor, logging out.
    func applicationWillTerminate(_ notification: Notification) {
        tell(agent: "quit")
    }

    // Opening the app again from Finder while it runs lands here: drop the menu down, so it is
    // clear where the agent lives. Not from inside the event: the menu would not open.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        DispatchQueue.main.async { self.statusItem.button?.performClick(nil) }
        return false
    }

    private func makeMenu() -> NSMenu {
        let menu = NSMenu()
        menu.autoenablesItems = false

        let title = NSMenuItem(title: "Paz Scan Agent \(version)", action: nil, keyEquivalent: "")
        title.isEnabled = false
        menu.addItem(title)
        stateItem.isEnabled = false
        menu.addItem(stateItem)
        menu.addItem(.separator())

        let logs = NSMenuItem(title: "View Live Logs…", action: #selector(showLogs), keyEquivalent: "")
        logs.target = self
        menu.addItem(logs)
        let folder = NSMenuItem(title: "Open Logs Folder", action: #selector(openLogsFolder), keyEquivalent: "")
        folder.target = self
        menu.addItem(folder)
        menu.addItem(.separator())

        let quit = NSMenuItem(title: "Quit Paz Scan Agent", action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        quit.target = NSApp
        menu.addItem(quit)
        return menu
    }

    /// Never shown (an accessory app has no menu bar of its own), but key equivalents still go
    /// through it: without these, ⌘C, ⌘A, ⌘F and ⌘W do nothing in the log window.
    private static func keyboardMenu() -> NSMenu {
        let main = NSMenu()

        let window = NSMenu(title: "Window")
        window.addItem(withTitle: "Close", action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        main.addItem(withTitle: "Window", action: nil, keyEquivalent: "").submenu = window

        let edit = NSMenu(title: "Edit")
        edit.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        let finds: [(String, String, NSEvent.ModifierFlags, NSTextFinder.Action)] = [
            ("Find…", "f", [.command], .showFindInterface),
            ("Find Next", "g", [.command], .nextMatch),
            ("Find Previous", "g", [.command, .shift], .previousMatch),
        ]
        for (title, key, modifiers, action) in finds {
            let item = edit.addItem(withTitle: title, action: #selector(NSTextView.performFindPanelAction(_:)), keyEquivalent: key)
            item.keyEquivalentModifierMask = modifiers
            item.tag = action.rawValue
        }
        main.addItem(withTitle: "Edit", action: nil, keyEquivalent: "").submenu = edit
        return main
    }

    private func show(_ state: State, _ text: String) {
        guard let button = statusItem.button else { return }
        let symbol = state == .error ? "exclamationmark.triangle" : "scanner"
        if let image = NSImage(systemSymbolName: symbol, accessibilityDescription: "Paz Scan Agent") {
            image.isTemplate = true
            button.image = image
            button.title = ""
        } else {
            button.image = nil
            button.title = state == .error ? "Paz ⚠︎" : "Paz"
        }
        // Dimmed while starting, as menu bar icons are when they are not ready yet.
        button.appearsDisabled = state == .starting
        button.toolTip = "Paz Scan Agent — \(text)"
        stateItem.title = text.count > 100 ? String(text.prefix(99)) + "…" : text
        stateItem.toolTip = text
    }

    private func listenToAgent() {
        Thread.detachNewThread { [weak self] in
            while let line = readLine() {
                DispatchQueue.main.async { self?.handle(line) }
            }
            // The agent has gone; it no longer needs an icon.
            DispatchQueue.main.async { exit(0) }
        }
    }

    private func handle(_ line: String) {
        let parts = line.split(separator: " ", maxSplits: 2, omittingEmptySubsequences: false)
        guard parts.count >= 2, parts[0] == "state" else { return }
        let text = parts.count > 2 ? String(parts[2]) : ""
        switch parts[1] {
        case "running": show(.running, text)
        case "error": show(.error, text)
        default: break
        }
    }

    @objc private func showLogs() {
        if logWindow == nil {
            let controller = LogWindowController(directory: logDirectory)
            controller.onClose = { [weak self] in self?.logWindow = nil }
            logWindow = controller
        }
        // Not activate(), macOS 14's polite version: from a menu bar app it often leaves the window
        // behind the app in front, without keyboard focus.
        NSApp.activate(ignoringOtherApps: true)
        logWindow?.showWindow(nil)
        logWindow?.window?.makeKeyAndOrderFront(nil)
        // Up front even if another app keeps the focus.
        logWindow?.window?.orderFrontRegardless()
    }

    @objc private func openLogsFolder() {
        try? FileManager.default.createDirectory(at: logDirectory, withIntermediateDirectories: true)
        NSWorkspace.shared.open(logDirectory)
    }
}

let logs = option("--logs").map { URL(fileURLWithPath: $0, isDirectory: true) }
    ?? FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Application Support/Paz Scan Agent/logs", isDirectory: true)

MainActor.assumeIsolated {
    let delegate = MenuBarApp(version: option("--version") ?? "", logDirectory: logs)
    let app = NSApplication.shared
    // No Dock icon and no app menu: the menu bar icon is the whole interface.
    app.setActivationPolicy(.accessory)
    app.delegate = delegate
    app.run()
}
