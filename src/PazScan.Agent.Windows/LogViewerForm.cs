using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PazScan.Agent.Windows;

internal sealed class LogViewerForm : Form
{
    private const int TailBytes = 256 * 1024;
    private const int MaxDisplayCharacters = 500_000;

    private readonly string _directory;
    private readonly Func<string?> _currentFile;
    private readonly RichTextBox _text;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly CheckBox _pause;
    private readonly CheckBox _follow;
    private readonly Label _status;
    private string? _file;
    private long _position;

    public LogViewerForm(string logDirectory, Func<string?> currentFile)
    {
        _directory = logDirectory;
        _currentFile = currentFile;
        Text = "Paz Scan Agent — live logs";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1000, 650);
        MinimumSize = new Size(600, 400);

        _text = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            WordWrap = false,
            Font = new Font("Consolas", 9f),
            BackColor = Color.FromArgb(24, 24, 24),
            ForeColor = Color.Gainsboro,
            BorderStyle = BorderStyle.None,
            DetectUrls = false,
            HideSelection = false,
        };

        _pause = new CheckBox { Text = "Pause", AutoSize = true, Padding = new Padding(8, 8, 0, 0) };
        _follow = new CheckBox { Text = "Follow", Checked = true, AutoSize = true, Padding = new Padding(8, 8, 0, 0) };
        _status = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
            ForeColor = SystemColors.GrayText,
        };

        var toolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
            Padding = new Padding(4),
        };
        toolbar.Controls.Add(Button("Open folder", (_, _) => OpenFolder()));
        toolbar.Controls.Add(Button("Copy all", (_, _) => CopyAll()));
        toolbar.Controls.Add(Button("Clear view", (_, _) => _text.Clear()));
        toolbar.Controls.Add(_pause);
        toolbar.Controls.Add(_follow);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(toolbar, 0, 0);
        layout.Controls.Add(_text, 0, 1);
        layout.Controls.Add(_status, 0, 2);
        Controls.Add(layout);

        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => RefreshLog();
        _timer.Start();
        RefreshLog();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }

    private static Button Button(string text, EventHandler click)
    {
        var button = new Button { Text = text, AutoSize = true, Padding = new Padding(4, 2, 4, 2) };
        button.Click += click;
        return button;
    }

    private void RefreshLog()
    {
        if (_pause.Checked) return;
        var file = _currentFile();
        if (file is null || !File.Exists(file)) file = NewestLog();
        if (file is null)
        {
            _status.Text = $"No log file in {_directory}";
            return;
        }

        if (!string.Equals(file, _file, StringComparison.OrdinalIgnoreCase))
        {
            _file = file;
            _position = 0;
            _text.Clear();
        }

        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < _position)
            {
                _position = 0;
                _text.Clear();
            }
            if (_position == 0)
            {
                _position = Math.Max(0, stream.Length - TailBytes);
                if (_position > 0) _text.AppendText("… earlier lines omitted …" + Environment.NewLine);
            }

            stream.Seek(_position, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
            var chunk = reader.ReadToEnd();
            _position = stream.Position;
            if (chunk.Length > 0) Append(chunk);

            var info = new FileInfo(file);
            _status.Text = $"{Path.GetFileName(file)} · {info.Length:N0} bytes · updated {info.LastWriteTime:HH:mm:ss}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _status.Text = $"{Path.GetFileName(file)} · waiting for the agent to write";
        }
    }

    private void Append(string chunk)
    {
        _text.AppendText(chunk);
        if (_text.TextLength > MaxDisplayCharacters)
        {
            _text.Select(0, _text.TextLength - MaxDisplayCharacters);
            _text.SelectedText = "";
        }
        if (_follow.Checked)
        {
            _text.SelectionStart = _text.TextLength;
            _text.ScrollToCaret();
        }
    }

    private string? NewestLog()
    {
        try
        {
            return new DirectoryInfo(_directory).GetFiles("agent-*.log")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()?.FullName;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void OpenFolder()
    {
        Directory.CreateDirectory(_directory);
        Process.Start(new ProcessStartInfo(_directory) { UseShellExecute = true });
    }

    private void CopyAll()
    {
        try { Clipboard.SetText(_text.Text); }
        catch (ExternalException) { }
    }
}
