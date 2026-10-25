import AppKit

/// The live log window: tails the newest log file in the agent's log folder, as "View live logs"
/// does on Windows. Warnings show in orange, errors in red, debug lines dimmed.
@MainActor
final class LogWindowController: NSWindowController, NSWindowDelegate {
    private static let tailBytes: UInt64 = 256 * 1024
    private static let maxCharacters = 500_000
    private static let font = NSFont.monospacedSystemFont(ofSize: 11, weight: .regular)

    var onClose: (() -> Void)?

    private let directory: URL
    private let textView = NSTextView(usingTextLayoutManager: false)
    private let status = NSTextField(labelWithString: "")
    private var pause: NSButton!
    private var follow: NSButton!
    private var timer: Timer?

    private var file: URL?
    private var position: UInt64 = 0
    /// The end of the last read when it stopped mid-line; the rest comes with the next read.
    private var partialLine = Data()
    /// After jumping into the middle of a large file, the first line read is a fragment.
    private var skippingFragment = false
    /// The level of the last entry, which its continuation lines (a stack trace) share.
    private var level = Level.information

    init(directory: URL) {
        self.directory = directory
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 1000, height: 650),
            styleMask: [.titled, .closable, .miniaturizable, .resizable],
            backing: .buffered,
            defer: false)
        super.init(window: window)

        window.title = "Paz Scan Agent — Live Logs"
        window.minSize = NSSize(width: 600, height: 400)
        window.isReleasedWhenClosed = false
        window.delegate = self
        if !window.setFrameUsingName("LiveLogs") { window.center() }
        window.setFrameAutosaveName("LiveLogs")

        layOut(in: window)

        // Holds on to this controller until the window closes and invalidates it.
        let timer = Timer(timeInterval: 1, target: self, selector: #selector(tick), userInfo: nil, repeats: true)
        // .common: keep updating while the window is being resized or scrolled.
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
        refresh()
    }

    @objc private func tick() {
        refresh()
    }

    required init?(coder: NSCoder) { fatalError("not used") }

    override func showWindow(_ sender: Any?) {
        super.showWindow(sender)
        // The first read happened before the window had a size to scroll in.
        if follow.state == .on { scrollToEnd() }
    }

    func windowWillClose(_ notification: Notification) {
        timer?.invalidate()
        timer = nil
        onClose?()
    }

    // MARK: Layout

    private func layOut(in window: NSWindow) {
        // TextKit 1 (usingTextLayoutManager: false) handles long, unwrapped plain text steadily.
        textView.isEditable = false
        textView.isSelectable = true
        textView.isRichText = false
        textView.allowsUndo = false
        textView.usesFindBar = true
        textView.isIncrementalSearchingEnabled = true
        textView.font = Self.font
        textView.textColor = .textColor
        textView.backgroundColor = .textBackgroundColor
        textView.textContainerInset = NSSize(width: 6, height: 6)
        // One entry per line, however long: scroll sideways rather than wrap.
        textView.minSize = .zero
        textView.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        textView.isVerticallyResizable = true
        textView.isHorizontallyResizable = true
        textView.autoresizingMask = [.width, .height]
        textView.textContainer?.widthTracksTextView = false
        textView.textContainer?.containerSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)

        let scroll = NSScrollView()
        scroll.hasVerticalScroller = true
        scroll.hasHorizontalScroller = true
        scroll.autohidesScrollers = true
        scroll.borderType = .noBorder
        scroll.documentView = textView

        pause = NSButton(checkboxWithTitle: "Pause", target: nil, action: nil)
        follow = NSButton(checkboxWithTitle: "Follow", target: self, action: #selector(followChanged))
        follow.state = .on
        let bar = NSStackView(views: [
            NSButton(title: "Show in Finder", target: self, action: #selector(showInFinder)),
            NSButton(title: "Copy All", target: self, action: #selector(copyAll)),
            NSButton(title: "Clear View", target: self, action: #selector(clearView)),
            pause,
            follow,
        ])
        bar.orientation = .horizontal
        bar.spacing = 10
        bar.setCustomSpacing(18, after: bar.views[2])

        status.font = .systemFont(ofSize: NSFont.smallSystemFontSize)
        status.textColor = .secondaryLabelColor
        status.lineBreakMode = .byTruncatingMiddle
        status.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)

        let top = NSBox()
        top.boxType = .separator
        let bottom = NSBox()
        bottom.boxType = .separator

        let content = NSView()
        for view in [bar, top, scroll, bottom, status] as [NSView] {
            view.translatesAutoresizingMaskIntoConstraints = false
            content.addSubview(view)
        }
        NSLayoutConstraint.activate([
            bar.topAnchor.constraint(equalTo: content.topAnchor, constant: 10),
            bar.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 12),
            bar.trailingAnchor.constraint(lessThanOrEqualTo: content.trailingAnchor, constant: -12),

            top.topAnchor.constraint(equalTo: bar.bottomAnchor, constant: 10),
            top.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            top.trailingAnchor.constraint(equalTo: content.trailingAnchor),

            scroll.topAnchor.constraint(equalTo: top.bottomAnchor),
            scroll.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            scroll.trailingAnchor.constraint(equalTo: content.trailingAnchor),

            bottom.topAnchor.constraint(equalTo: scroll.bottomAnchor),
            bottom.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            bottom.trailingAnchor.constraint(equalTo: content.trailingAnchor),

            status.topAnchor.constraint(equalTo: bottom.bottomAnchor, constant: 5),
            status.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 12),
            status.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -12),
            status.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: -6),
        ])
        window.contentView = content
    }

    // MARK: Buttons

    @objc private func showInFinder() {
        if let file, FileManager.default.fileExists(atPath: file.path) {
            NSWorkspace.shared.activateFileViewerSelecting([file])
        } else {
            try? FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            NSWorkspace.shared.open(directory)
        }
    }

    @objc private func copyAll() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(textView.string, forType: .string)
    }

    @objc private func clearView() {
        textView.string = ""
    }

    @objc private func followChanged() {
        if follow.state == .on { scrollToEnd() }
    }

    // MARK: Tailing

    private func refresh() {
        guard pause.state == .off else { return }
        guard let newest = newestLog() else {
            status.stringValue = "No log file yet in \(directory.path)"
            return
        }

        if newest.path != file?.path {
            // A new day, or the last file reached its size limit.
            if file != nil { appendNote("── continued in \(newest.lastPathComponent) ──") }
            file = newest
            position = 0
            partialLine = Data()
            skippingFragment = false
        }

        do {
            let handle = try FileHandle(forReadingFrom: newest)
            defer { try? handle.close() }
            let length = try handle.seekToEnd()
            if length < position {
                // Replaced or cut short: read it again from the top.
                appendNote("── \(newest.lastPathComponent) started over ──")
                position = 0
                partialLine = Data()
                skippingFragment = false
            }
            if position == 0 && length > Self.tailBytes {
                position = length - Self.tailBytes
                skippingFragment = true
                appendNote("… earlier lines omitted …")
            }
            try handle.seek(toOffset: position)
            let data = try handle.readToEnd() ?? Data()
            position += UInt64(data.count)
            consume(data)

            let modified = (try? newest.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate
            let size = ByteCountFormatter.string(fromByteCount: Int64(position), countStyle: .file)
            let time = modified.map { DateFormatter.localizedString(from: $0, dateStyle: .none, timeStyle: .medium) } ?? "—"
            status.stringValue = "\(newest.lastPathComponent) · \(size) · updated \(time)"
        } catch {
            status.stringValue = "\(newest.lastPathComponent) · waiting for the agent to write"
        }
    }

    /// The agent writes one file a day and starts another at 5 MB, so the one written last is current.
    private func newestLog() -> URL? {
        let key = URLResourceKey.contentModificationDateKey
        guard let files = try? FileManager.default.contentsOfDirectory(
            at: directory, includingPropertiesForKeys: [key], options: [.skipsHiddenFiles])
        else { return nil }

        var newest: (url: URL, modified: Date)?
        for url in files where url.lastPathComponent.hasPrefix("agent-") && url.pathExtension == "log" {
            let modified: Date = (try? url.resourceValues(forKeys: [key]))?.contentModificationDate ?? .distantPast
            if let best = newest {
                if modified < best.modified { continue }
                if modified == best.modified && url.lastPathComponent < best.url.lastPathComponent { continue }
            }
            newest = (url, modified)
        }
        return newest?.url
    }

    /// Shows the complete lines read so far. A newline byte never occurs inside a UTF-8 character,
    /// so splitting there never splits a character.
    private func consume(_ data: Data) {
        var bytes = partialLine + data
        if skippingFragment {
            guard let newline = bytes.firstIndex(of: 0x0A) else {
                partialLine = Data()
                return
            }
            bytes = bytes.suffix(from: bytes.index(after: newline))
            skippingFragment = false
        }
        guard let lastNewline = bytes.lastIndex(of: 0x0A) else {
            partialLine = Data(bytes)
            return
        }
        partialLine = Data(bytes.suffix(from: bytes.index(after: lastNewline)))
        let text = String(decoding: bytes.prefix(upTo: lastNewline), as: UTF8.self)

        let output = NSMutableAttributedString()
        for line in text.split(separator: "\n", omittingEmptySubsequences: false) {
            if let entry = Level(line: line) { level = entry }
            output.append(NSAttributedString(string: line + "\n", attributes: [
                .font: Self.font,
                .foregroundColor: level.color,
            ]))
        }
        append(output)
    }

    private func appendNote(_ note: String) {
        append(NSAttributedString(string: note + "\n", attributes: [
            .font: Self.font,
            .foregroundColor: NSColor.tertiaryLabelColor,
        ]))
    }

    private func append(_ text: NSAttributedString) {
        guard text.length > 0, let storage = textView.textStorage else { return }
        storage.beginEditing()
        storage.append(text)
        let excess = storage.length - Self.maxCharacters
        if excess > 0 {
            // Drop whole lines from the top.
            let string = storage.string as NSString
            let newline = string.range(of: "\n", range: NSRange(location: excess, length: string.length - excess))
            storage.deleteCharacters(in: NSRange(location: 0, length: newline.location == NSNotFound ? excess : newline.location + 1))
        }
        storage.endEditing()
        if follow.state == .on { scrollToEnd() }
    }

    private func scrollToEnd() {
        textView.scrollRangeToVisible(NSRange(location: textView.string.utf16.count, length: 0))
    }
}

/// How a line of the log is coloured, from the level column the agent writes:
/// "2026-10-25 14:03:22.123 Information PazScan.Agent: message".
private enum Level {
    case quiet, information, warning, error

    var color: NSColor {
        switch self {
        case .quiet: return .secondaryLabelColor
        case .information: return .textColor
        case .warning: return .systemOrange
        case .error: return .systemRed
        }
    }

    /// Nil for a line that does not start a new entry, such as an exception's stack trace.
    init?(line: Substring) {
        let bytes = Array(line.utf8.prefix(36))
        guard bytes.count > 24, bytes[4] == 0x2D, bytes[10] == 0x20, bytes[13] == 0x3A, bytes[23] == 0x20 else { return nil }
        let name = String(decoding: bytes[24...].prefix { $0 != 0x20 }, as: UTF8.self)
        switch name {
        case "Trace", "Debug": self = .quiet
        case "Information": self = .information
        case "Warning": self = .warning
        case "Error", "Critical": self = .error
        default: return nil
        }
    }
}
