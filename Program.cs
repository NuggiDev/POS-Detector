using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace PosCashDetector;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new DetectorForm(AppConfig.Load()));
    }
}

internal sealed class DetectorForm : Form
{
    private readonly AppConfig _config;
    private readonly CsvPaymentLog _log;
    private readonly ConcurrentQueue<AppEvent> _events = new();
    private readonly System.Windows.Forms.Timer _eventTimer = new();
    private SerialComReader? _reader;

    private readonly Label _statusLabel = new();
    private readonly Label _lastDetectionLabel = new();
    private readonly TextBox _keyboardInput = new();
    private readonly TextBox _portInput = new();
    private readonly NumericUpDown _baudInput = new();
    private readonly Button _serialButton = new();
    private readonly DataGridView _grid = new();

    public DetectorForm(AppConfig config)
    {
        _config = config;
        _log = new CsvPaymentLog(Path.GetFullPath(config.LogFile));

        Text = "POS / Cash Detector";
        Width = 780;
        Height = 520;
        MinimumSize = new Size(640, 430);
        KeyPreview = true;

        BuildUi();
        BindEvents();

        _eventTimer.Interval = 150;
        _eventTimer.Tick += (_, _) => DrainEvents();
        _eventTimer.Start();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 1,
            RowCount = 6
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var title = new Label
        {
            Text = "Payment detector",
            AutoSize = true,
            Font = new Font("Segoe UI", 18, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 4)
        };
        root.Controls.Add(title, 0, 0);

        _statusLabel.Text = "Ready. Scan/tap POS reader, start serial, or press F9 for cash.";
        _statusLabel.AutoSize = true;
        _statusLabel.Margin = new Padding(0, 0, 0, 16);
        root.Controls.Add(_statusLabel, 0, 1);

        _lastDetectionLabel.Text = "No payment detected yet.";
        _lastDetectionLabel.AutoSize = true;
        _lastDetectionLabel.Font = new Font("Segoe UI", 14, FontStyle.Regular);
        _lastDetectionLabel.Margin = new Padding(0, 0, 0, 14);
        root.Controls.Add(_lastDetectionLabel, 0, 2);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 12),
            WrapContents = true
        };
        var cashButton = new Button { Text = "Cash (F9)", Width = 120, Height = 34 };
        cashButton.Click += (_, _) => MarkCash();
        var testButton = new Button { Text = "Test POS", Width = 120, Height = 34 };
        testButton.Click += (_, _) => DetectPos("manual", "TEST POS APPROVED");
        actions.Controls.Add(cashButton);
        actions.Controls.Add(testButton);
        root.Controls.Add(actions, 0, 3);

        var signalPanel = new TableLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            Margin = new Padding(0, 0, 0, 12)
        };
        signalPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        signalPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        signalPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        signalPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));

        _keyboardInput.PlaceholderText = "Keyboard reader input, then Enter";
        _keyboardInput.Dock = DockStyle.Fill;
        _keyboardInput.Font = new Font("Consolas", 11);

        _portInput.PlaceholderText = "COM3";
        _portInput.Text = "COM3";
        _portInput.Dock = DockStyle.Fill;

        _baudInput.Minimum = 1200;
        _baudInput.Maximum = 115200;
        _baudInput.Value = _config.DefaultBaudRate;
        _baudInput.Increment = 1200;
        _baudInput.Dock = DockStyle.Fill;

        _serialButton.Text = "Start Serial";
        _serialButton.Dock = DockStyle.Fill;
        _serialButton.Height = 30;

        signalPanel.Controls.Add(_keyboardInput, 0, 0);
        signalPanel.Controls.Add(_portInput, 1, 0);
        signalPanel.Controls.Add(_baudInput, 2, 0);
        signalPanel.Controls.Add(_serialButton, 3, 0);
        root.Controls.Add(signalPanel, 0, 4);

        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.Columns.Add("createdAt", "Time");
        _grid.Columns.Add("kind", "Kind");
        _grid.Columns.Add("source", "Source");
        _grid.Columns.Add("reason", "Reason");
        _grid.Columns.Add("signal", "Signal");
        root.Controls.Add(_grid, 0, 5);

        _keyboardInput.Focus();
    }

    private void BindEvents()
    {
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F9)
            {
                MarkCash();
                e.Handled = true;
            }
        };

        _keyboardInput.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter)
            {
                return;
            }

            var signal = _keyboardInput.Text.Trim();
            _keyboardInput.Clear();
            if (signal.Length > 0)
            {
                DetectPos("keyboard", signal);
            }

            e.SuppressKeyPress = true;
        };

        _serialButton.Click += (_, _) => ToggleSerial();
        FormClosing += (_, _) => _reader?.Stop();
    }

    private void ToggleSerial()
    {
        if (_reader is not null)
        {
            _reader.Stop();
            _reader = null;
            _serialButton.Text = "Start Serial";
            _statusLabel.Text = "Serial stopped.";
            return;
        }

        var port = _portInput.Text.Trim();
        if (port.Length == 0)
        {
            _statusLabel.Text = "Enter a COM port first, for example COM3.";
            return;
        }

        _reader = new SerialComReader(port, (int)_baudInput.Value, _events);
        _reader.Start();
        _serialButton.Text = "Stop Serial";
    }

    private void DrainEvents()
    {
        while (_events.TryDequeue(out var appEvent))
        {
            if (appEvent.Type == AppEventType.Signal)
            {
                DetectPos("serial", appEvent.Payload);
            }
            else
            {
                _statusLabel.Text = appEvent.Payload;
            }
        }
    }

    private void DetectPos(string source, string signal)
    {
        var result = SignalClassifier.Classify(signal, _config);
        Record(new Detection(result.Kind, source, signal, result.Reason, DateTime.Now));
    }

    private void MarkCash()
    {
        Record(new Detection("CASH", "manual", "no POS signal", "cash selected", DateTime.Now));
    }

    private void Record(Detection detection)
    {
        _log.Append(detection);
        _lastDetectionLabel.Text = $"{detection.Kind} detected from {detection.Source} at {detection.CreatedAt:yyyy-MM-dd HH:mm:ss}";
        _statusLabel.Text = detection.Reason;
        _grid.Rows.Insert(0, detection.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"), detection.Kind, detection.Source, detection.Reason, detection.Signal);
    }
}

internal sealed record Detection(string Kind, string Source, string Signal, string Reason, DateTime CreatedAt);

internal sealed record Classification(string Kind, string Reason);

internal static class SignalClassifier
{
    public static Classification Classify(string signal, AppConfig config)
    {
        var normalized = signal.Trim();
        if (normalized.Length < config.MinimumSignalLength)
        {
            return new Classification("UNKNOWN", "signal too short");
        }

        foreach (var keyword in config.PosKeywords)
        {
            if (normalized.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return new Classification("POS", "matched POS keyword");
            }
        }

        return new Classification("POS", "reader fired a signal");
    }
}

internal sealed class CsvPaymentLog
{
    private readonly string _path;

    public CsvPaymentLog(string path)
    {
        _path = path;
        if (!File.Exists(_path))
        {
            File.WriteAllText(_path, "created_at,kind,source,reason,signal" + Environment.NewLine, Encoding.UTF8);
        }
    }

    public void Append(Detection detection)
    {
        var line = string.Join(
            ",",
            Escape(detection.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")),
            Escape(detection.Kind),
            Escape(detection.Source),
            Escape(detection.Reason),
            Escape(detection.Signal));
        File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
    }

    private static string Escape(string value)
    {
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}

internal sealed class AppConfig
{
    private const string ConfigFileName = "detector_config.json";

    public List<string> PosKeywords { get; set; } = ["APPROVED", "AUTH", "PAID", "CARD", "POS", "TRANSACTION"];
    public int MinimumSignalLength { get; set; } = 3;
    public int DefaultBaudRate { get; set; } = 9600;
    public string LogFile { get; set; } = "payments.csv";

    public static AppConfig Load()
    {
        if (!File.Exists(ConfigFileName))
        {
            var defaultConfig = new AppConfig();
            File.WriteAllText(ConfigFileName, JsonSerializer.Serialize(defaultConfig, JsonOptions()), Encoding.UTF8);
            return defaultConfig;
        }

        var configJson = File.ReadAllText(ConfigFileName, Encoding.UTF8);
        return JsonSerializer.Deserialize<AppConfig>(configJson, JsonOptions()) ?? new AppConfig();
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions { WriteIndented = true };
    }
}

internal enum AppEventType
{
    Status,
    Signal
}

internal sealed record AppEvent(AppEventType Type, string Payload);

internal sealed class SerialComReader
{
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly ConcurrentQueue<AppEvent> _events;
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;

    public SerialComReader(string portName, int baudRate, ConcurrentQueue<AppEvent> events)
    {
        _portName = portName;
        _baudRate = baudRate;
        _events = events;
    }

    public void Start()
    {
        _thread = new Thread(ReadLoop) { IsBackground = true };
        _thread.Start();
    }

    public void Stop()
    {
        _stop.Cancel();
    }

    private void ReadLoop()
    {
        var devicePath = _portName.StartsWith(@"\\.\", StringComparison.Ordinal) ? _portName : @"\\.\" + _portName;
        using var handle = NativeSerial.Open(devicePath);
        if (handle.IsInvalid)
        {
            _events.Enqueue(new AppEvent(AppEventType.Status, $"Could not open {_portName}. Check the COM port and permissions."));
            return;
        }

        if (!NativeSerial.Configure(handle, _baudRate))
        {
            _events.Enqueue(new AppEvent(AppEventType.Status, $"Could not configure {_portName} at {_baudRate} baud."));
            return;
        }

        _events.Enqueue(new AppEvent(AppEventType.Status, $"Listening on {_portName} at {_baudRate} baud."));

        var buffer = new byte[256];
        var pending = new StringBuilder();
        while (!_stop.IsCancellationRequested)
        {
            if (!NativeSerial.Read(handle, buffer, out var bytesRead))
            {
                _events.Enqueue(new AppEvent(AppEventType.Status, $"Read error on {_portName}."));
                return;
            }

            if (bytesRead == 0)
            {
                Thread.Sleep(25);
                continue;
            }

            pending.Append(Encoding.ASCII.GetString(buffer, 0, bytesRead));
            FlushCompleteLines(pending);
        }
    }

    private void FlushCompleteLines(StringBuilder pending)
    {
        while (true)
        {
            var text = pending.ToString();
            var newlineIndex = text.IndexOfAny(['\r', '\n']);
            if (newlineIndex < 0)
            {
                if (text.Length >= 32)
                {
                    pending.Clear();
                    _events.Enqueue(new AppEvent(AppEventType.Signal, text.Trim()));
                }
                return;
            }

            var line = text[..newlineIndex].Trim();
            pending.Remove(0, newlineIndex + 1);
            if (line.Length > 0)
            {
                _events.Enqueue(new AppEvent(AppEventType.Signal, line));
            }
        }
    }
}

internal static partial class NativeSerial
{
    private const uint GenericRead = 0x80000000;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;

    public static SafeFileHandle Open(string devicePath)
    {
        return CreateFile(devicePath, GenericRead, 0, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
    }

    public static bool Configure(SafeFileHandle handle, int baudRate)
    {
        var dcb = new Dcb { DCBlength = (uint)Marshal.SizeOf<Dcb>() };
        if (!GetCommState(handle, ref dcb))
        {
            return false;
        }

        dcb.BaudRate = (uint)baudRate;
        dcb.ByteSize = 8;
        dcb.Parity = 0;
        dcb.StopBits = 0;
        dcb.Flags = 1;

        var timeouts = new CommTimeouts
        {
            ReadIntervalTimeout = 50,
            ReadTotalTimeoutConstant = 50,
            ReadTotalTimeoutMultiplier = 10
        };

        return SetCommState(handle, ref dcb) && SetCommTimeouts(handle, ref timeouts);
    }

    public static bool Read(SafeFileHandle handle, byte[] buffer, out int bytesRead)
    {
        var ok = ReadFile(handle, buffer, buffer.Length, out var read, IntPtr.Zero);
        bytesRead = (int)read;
        return ok;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCommState(SafeFileHandle handle, ref Dcb dcb);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCommState(SafeFileHandle handle, ref Dcb dcb);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCommTimeouts(SafeFileHandle handle, ref CommTimeouts timeouts);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadFile(
        SafeFileHandle file,
        [Out] byte[] buffer,
        int bytesToRead,
        out uint bytesRead,
        IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct Dcb
    {
        public uint DCBlength;
        public uint BaudRate;
        public uint Flags;
        public ushort WReserved;
        public ushort XonLim;
        public ushort XoffLim;
        public byte ByteSize;
        public byte Parity;
        public byte StopBits;
        public byte XonChar;
        public byte XoffChar;
        public byte ErrorChar;
        public byte EofChar;
        public byte EvtChar;
        public ushort WReserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommTimeouts
    {
        public uint ReadIntervalTimeout;
        public uint ReadTotalTimeoutMultiplier;
        public uint ReadTotalTimeoutConstant;
        public uint WriteTotalTimeoutMultiplier;
        public uint WriteTotalTimeoutConstant;
    }
}
