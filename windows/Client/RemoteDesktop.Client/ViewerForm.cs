using System.ComponentModel;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.Client;

/// <summary>
/// 원격 화면 창: 화면 표시, 마우스/키보드 입력, 도구 모음(모니터, 특수 키, 전원, 파일, 클립보드, 소리), 자동 재연결.
/// Ctrl+Alt+Enter: 전체 화면 전환 (원격으로 보내지 않는 유일한 단축키)
/// </summary>
internal sealed class ViewerForm : Form
{
    private const int VkReturn = 0x0D;
    private static readonly TimeSpan ReconnectWindow = TimeSpan.FromSeconds(60);

    private readonly Func<CancellationToken, Task<RemoteHostConnection>>? _reconnect;
    private readonly RemoteView _view = new() { Dock = DockStyle.Fill };
    private readonly ToolStrip _toolbar = new() { GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System };
    private readonly ToolStripLabel _qualityLabel = new() { Alignment = ToolStripItemAlignment.Right, ForeColor = Color.DimGray };
    private readonly ToolStripDropDownButton _monitorButton = new("모니터");
    private readonly ToolStripDropDownButton _resolutionButton = new("해상도") { Tag = "display" };
    private readonly ToolStripButton _clipboardButton = new("클립보드 공유") { CheckOnClick = true };
    private readonly ToolStripButton _audioButton = new("소리") { CheckOnClick = true };
    private readonly System.Windows.Forms.Timer _titleTimer = new() { Interval = 1000 };
    private readonly HashSet<string> _pressedKeys = new();
    private readonly HashSet<MouseButton> _pressedButtons = new();
    private readonly CancellationTokenSource _closing = new();

    private RemoteHostConnection _connection;
    private ClientFeatures _features;
    private InputSender _input;
    private KeyboardHook? _keyboardHook;
    private StreamStatsMessage? _stats;
    private int _framesThisSecond;
    private int _fps;
    private bool _reconnecting;
    private FormWindowState _windowStateBeforeFullScreen;

    public ViewerForm(RemoteHostConnection connection, Func<CancellationToken, Task<RemoteHostConnection>>? reconnect = null)
    {
        _connection = connection;
        _reconnect = reconnect;
        _features = new ClientFeatures(connection);
        _input = new InputSender(connection);

        Text = $"{connection.HostName} ({connection.HostId})";
        BackColor = Color.Black;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;

        BuildToolbar();
        Controls.Add(_view);
        Controls.Add(_toolbar);
        ClientSize = FitInitialSize(connection.ScreenWidth, connection.ScreenHeight) + new Size(0, _toolbar.Height);

        _view.MouseMove += (_, e) => SendMouseMove(e.Location);
        _view.MouseDown += OnViewMouseDown;
        _view.MouseUp += OnViewMouseUp;
        _view.MouseWheel += (_, e) => _input.Enqueue(new MouseWheelMessage(0, e.Delta));
        _view.HorizontalWheel += delta => _input.Enqueue(new MouseWheelMessage(delta, 0));
        _view.DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        _view.DragDrop += async (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] files)
            {
                await UploadFilesAsync(files);
            }
        };

        _titleTimer.Tick += (_, _) => UpdateTitle();
        _titleTimer.Start();
        Attach(connection);
    }

    /// <summary>연결이 끊겨서 닫힌 경우 그 이유. 사용자가 닫았으면 null.</summary>
    public string? CloseReason { get; private set; }

    // ======================= 연결 연결/교체 =======================

    private void Attach(RemoteHostConnection connection)
    {
        _view.RemoteSize = new Size(connection.ScreenWidth, connection.ScreenHeight);
        connection.FrameReceived += OnFrameReceived;
        connection.Disconnected += OnDisconnected;
        connection.StatsReceived += OnStats;
        _features.RemoteClipboardReceived += OnRemoteClipboard;

        bool canInput = connection.HasFeature(HostFeatures.Input);
        _secureAttention = connection.HasFeature(HostFeatures.SecureAttention);
        _clipboardButton.Enabled = connection.HasFeature(HostFeatures.Clipboard);
        _audioButton.Enabled = connection.HasFeature(HostFeatures.Audio);
        if (_clipboardButton.Enabled && _clipboardButton.Checked)
        {
            _features.SetClipboardSync(true);
        }

        if (_audioButton.Enabled && _audioButton.Checked)
        {
            _ = _features.SetAudioAsync(true);
        }

        BuildMonitorMenu(connection.Monitors);
        foreach (ToolStripItem item in _toolbar.Items)
        {
            if (item.Tag is "input")
            {
                item.Enabled = canInput;
            }
            else if (item.Tag is "files")
            {
                item.Enabled = connection.HasFeature(HostFeatures.FileTransfer);
            }
            else if (item.Tag is "display")
            {
                item.Visible = connection.HasFeature(HostFeatures.Display);
            }
        }
    }

    private void Detach(RemoteHostConnection connection)
    {
        connection.FrameReceived -= OnFrameReceived;
        connection.Disconnected -= OnDisconnected;
        connection.StatsReceived -= OnStats;
        _features.RemoteClipboardReceived -= OnRemoteClipboard;
    }

    // 백그라운드 스레드에서 호출됩니다.
    private void OnFrameReceived(Bitmap frame, VideoFrameHeader header)
    {
        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed)
                {
                    frame.Dispose();
                    return;
                }

                _view.SetFrame(frame);
                _framesThisSecond++;

                // 화면에 반영한 뒤 ack를 보냅니다. → Host는 표시 속도에 맞춰 보냅니다.
                _ = _connection.SendAckAsync(header.FrameId);
            });
        }
        catch (InvalidOperationException)
        {
            frame.Dispose();
        }
    }

    private void OnStats(StreamStatsMessage stats) => _stats = stats;

    private void OnRemoteClipboard(string text)
    {
        try
        {
            BeginInvoke(() => _qualityLabel.ToolTipText = $"원격 클립보드: {(text.Length > 40 ? text[..40] + "..." : text)}");
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void OnDisconnected(string reason)
    {
        try
        {
            BeginInvoke(() => _ = HandleDisconnectAsync(reason));
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>자동 재연결: 1, 2, 4, 8초... 간격으로 최대 60초 동안 다시 연결합니다.</summary>
    private async Task HandleDisconnectAsync(string reason)
    {
        if (_reconnect is null || _reconnecting || _closing.IsCancellationRequested)
        {
            CloseReason = reason;
            Close();
            return;
        }

        _reconnecting = true;
        ReleaseAllInput();
        Detach(_connection);
        _features.Dispose();
        _input.Dispose();
        _connection.Dispose();

        var started = DateTime.UtcNow;
        TimeSpan delay = TimeSpan.FromSeconds(1);
        for (int attempt = 1; DateTime.UtcNow - started < ReconnectWindow && !_closing.IsCancellationRequested; attempt++)
        {
            _view.Overlay = $"연결이 끊겼습니다.\n{reason}\n\n다시 연결하는 중... ({attempt}번째 시도)";
            try
            {
                RemoteHostConnection connection = await _reconnect(_closing.Token);
                _connection = connection;
                _features = new ClientFeatures(connection);
                _input = new InputSender(connection);
                Attach(connection);
                connection.StartReceiving();
                _view.Overlay = null;
                _reconnecting = false;
                Log.Info("Reconnected");
                return;
            }
            catch (ConnectionFailedException exception) when (exception.ErrorCode is not null and not AuthErrorCodes.Busy)
            {
                reason = exception.Message; // 인증 실패 등은 다시 시도해도 소용없음
                break;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                reason = exception.Message;
            }

            try
            {
                await Task.Delay(delay, _closing.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(10).Ticks));
        }

        _reconnecting = false;
        if (!IsDisposed)
        {
            CloseReason = $"다시 연결하지 못했습니다: {reason}";
            Close();
        }
    }

    // ======================= 도구 모음 =======================

    private void BuildToolbar()
    {
        _toolbar.Items.Add(_monitorButton);
        _toolbar.Items.Add(_resolutionButton);
        _resolutionButton.DropDownItems.Add("(불러오는 중...)");
        _resolutionButton.DropDownOpening += async (_, _) => await LoadResolutionsAsync();

        var keys = new ToolStripDropDownButton("특수 키") { Tag = "input" };
        keys.DropDownItems.Add("Ctrl+Alt+Del", null, async (_, _) => await SendSasAsync());
        keys.DropDownItems.Add("작업 관리자 (Ctrl+Shift+Esc)", null, (_, _) => TapKeys("ControlLeft", "ShiftLeft", "Escape"));
        keys.DropDownItems.Add("Windows 키", null, (_, _) => TapKeys("MetaLeft"));
        keys.DropDownItems.Add("Alt+Tab", null, (_, _) => TapKeys("AltLeft", "Tab"));
        keys.DropDownItems.Add("한/영", null, (_, _) => TapKeys("Lang1"));
        keys.DropDownItems.Add("Print Screen", null, (_, _) => TapKeys("PrintScreen"));
        _toolbar.Items.Add(keys);

        var power = new ToolStripDropDownButton("전원") { Tag = "input" };
        power.DropDownItems.Add("화면 잠금", null, async (_, _) => await PowerAsync("lock", "원격 PC 화면을 잠글까요?"));
        power.DropDownItems.Add("로그아웃", null, async (_, _) => await PowerAsync("logoff", "원격 PC에서 로그아웃할까요?\n저장하지 않은 작업은 사라질 수 있습니다."));
        power.DropDownItems.Add("다시 시작", null, async (_, _) => await PowerAsync("restart", "원격 PC를 다시 시작할까요?\n5초 후 다시 시작합니다."));
        power.DropDownItems.Add("종료", null, async (_, _) => await PowerAsync("shutdown", "원격 PC를 종료할까요?\n종료하면 원격으로 다시 켤 수 없습니다."));
        _toolbar.Items.Add(power);

        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(new ToolStripButton("파일 보내기", null, async (_, _) => await ChooseAndUploadAsync()) { Tag = "files" });
        _toolbar.Items.Add(new ToolStripButton("파일 받기", null, async (_, _) => await DownloadAsync()) { Tag = "files" });
        _toolbar.Items.Add(new ToolStripSeparator());

        _clipboardButton.Checked = true;
        _clipboardButton.CheckedChanged += (_, _) => _features.SetClipboardSync(_clipboardButton.Checked);
        _audioButton.CheckedChanged += async (_, _) => await _features.SetAudioAsync(_audioButton.Checked);
        _toolbar.Items.Add(_clipboardButton);
        _toolbar.Items.Add(_audioButton);
        _toolbar.Items.Add(new ToolStripSeparator());
        _toolbar.Items.Add(new ToolStripButton("전체 화면", null, (_, _) => ToggleFullScreen()) { ToolTipText = "Ctrl+Alt+Enter" });
        _toolbar.Items.Add(_qualityLabel);
    }

    private void BuildMonitorMenu(MonitorInfo[] monitors)
    {
        _monitorButton.DropDownItems.Clear();
        _monitorButton.Visible = monitors.Length > 0;
        foreach (MonitorInfo monitor in monitors)
        {
            string label = $"모니터 {monitor.Index + 1}  ({monitor.Width}x{monitor.Height}){(monitor.Primary ? "  주 모니터" : "")}";
            _monitorButton.DropDownItems.Add(label, null, (_, _) => _ = _connection.SendAsync(new SelectMonitorMessage(monitor.Index)));
        }

        if (monitors.Length > 1)
        {
            _monitorButton.DropDownItems.Add(new ToolStripSeparator());
            _monitorButton.DropDownItems.Add("모든 모니터", null, (_, _) => _ = _connection.SendAsync(new SelectMonitorMessage(-1)));
        }
    }

    /// <summary>해상도 메뉴를 열 때 Host에서 목록을 받아 채웁니다.</summary>
    private async Task LoadResolutionsAsync()
    {
        DisplayModesMessage modes;
        try
        {
            modes = await _features.GetDisplayModesAsync(_closing.Token);
        }
        catch (Exception exception) when (exception is TimeoutException or InvalidOperationException)
        {
            _resolutionButton.DropDownItems.Clear();
            _resolutionButton.DropDownItems.Add(exception.Message).Enabled = false;
            return;
        }

        _resolutionButton.DropDownItems.Clear();
        _resolutionButton.DropDownItems.Add($"원래대로 ({modes.Original.Width}x{modes.Original.Height})", null,
            async (_, _) => await ChangeResolutionAsync(0, 0));

        // 이 창(원격 화면 영역)에 가장 잘 맞는, 창보다 크지 않은 해상도
        Size area = _view.ClientSize;
        DisplayMode? fit = modes.Modes
            .Where(m => m.Width <= area.Width && m.Height <= area.Height)
            .OrderByDescending(m => m.Width * m.Height)
            .FirstOrDefault();
        if (fit is not null)
        {
            _resolutionButton.DropDownItems.Add($"이 창 크기에 맞추기 ({fit.Width}x{fit.Height})", null,
                async (_, _) => await ChangeResolutionAsync(fit.Width, fit.Height));
        }

        _resolutionButton.DropDownItems.Add(new ToolStripSeparator());
        foreach (DisplayMode mode in modes.Modes)
        {
            var item = new ToolStripMenuItem($"{mode.Width} x {mode.Height}", null, async (_, _) => await ChangeResolutionAsync(mode.Width, mode.Height))
            {
                Checked = mode == modes.Current
            };
            _resolutionButton.DropDownItems.Add(item);
        }
    }

    private async Task ChangeResolutionAsync(int width, int height)
    {
        _qualityLabel.Text = width == 0 ? "원래 해상도로 되돌리는 중..." : $"{width}x{height}로 바꾸는 중...";
        try
        {
            DisplayResultMessage result = await _features.SetResolutionAsync(width, height, _closing.Token);
            _qualityLabel.Text = result.Success
                ? $"원격 해상도: {result.Current?.Width}x{result.Current?.Height} (연결을 끊으면 원래대로 돌아갑니다)"
                : $"실패: {result.Error}";
        }
        catch (TimeoutException)
        {
            _qualityLabel.Text = "해상도 변경 응답이 없습니다.";
        }
    }

    private bool _secureAttention;

    /// <summary>Ctrl+Alt+Del: Host가 Windows 서비스로 실행 중일 때만 (STEP 13)</summary>
    private async Task SendSasAsync()
    {
        if (!_secureAttention)
        {
            MessageBox.Show(this,
                "원격 PC의 Host가 Windows 서비스로 설치되어 있어야 Ctrl+Alt+Del을 보낼 수 있습니다.\n" +
                "(원격 PC에서 관리자 PowerShell로 scripts\\install-service.ps1 실행)\n\n" +
                "• 작업 관리자: [Ctrl+Shift+Esc]를 사용하세요.\n• 화면 잠금: [전원 → 화면 잠금]을 사용하세요.",
                "Ctrl+Alt+Del", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            SasResultMessage result = await _features.SendSasAsync(_closing.Token);
            if (!result.Success)
            {
                MessageBox.Show(this, result.Error ?? "실패했습니다.", "Ctrl+Alt+Del", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (TimeoutException)
        {
            MessageBox.Show(this, "응답이 없습니다.", "Ctrl+Alt+Del", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task PowerAsync(string action, string question)
    {
        if (MessageBox.Show(this, question, "전원", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        try
        {
            PowerResultMessage result = await _features.PowerAsync(action, _closing.Token);
            if (!result.Success)
            {
                MessageBox.Show(this, result.Error ?? "실패했습니다.", "전원", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (TimeoutException)
        {
            MessageBox.Show(this, "응답이 없습니다.", "전원", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task ChooseAndUploadAsync()
    {
        using var dialog = new OpenFileDialog { Multiselect = true, Title = "원격 PC로 보낼 파일" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            await UploadFilesAsync(dialog.FileNames);
        }
    }

    private async Task UploadFilesAsync(string[] paths)
    {
        foreach (string path in paths.Where(File.Exists))
        {
            string name = Path.GetFileName(path);
            long size = new FileInfo(path).Length;
            var progress = new Progress<long>(sent => _qualityLabel.Text = $"보내는 중: {name} {sent * 100 / Math.Max(size, 1)}%");
            try
            {
                FileResultMessage result = await _features.UploadAsync(path, progress, _closing.Token);
                _qualityLabel.Text = result.Success ? $"보냄: {result.SavedAs} (원격 PC의 공유 폴더\\Received)" : $"실패: {result.Error}";
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException)
            {
                _qualityLabel.Text = $"실패: {exception.Message}";
            }
        }
    }

    private async Task DownloadAsync()
    {
        FileListMessage list;
        try
        {
            list = await _features.GetFileListAsync(_closing.Token);
        }
        catch (TimeoutException)
        {
            MessageBox.Show(this, "파일 목록을 받지 못했습니다.", "파일 받기", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        string? name = FileListDialog.Choose(this, list);
        if (name is null)
        {
            return;
        }

        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        _qualityLabel.Text = $"받는 중: {name}";
        FileResultMessage result = await _features.DownloadAsync(name, folder, _closing.Token);
        _qualityLabel.Text = result.Success ? $"받음: {Path.Combine(folder, result.SavedAs!)}" : $"실패: {result.Error}";
    }

    private void UpdateTitle()
    {
        _fps = _framesThisSecond;
        _framesThisSecond = 0;
        Bitmap? frame = _view.Frame;
        string size = frame is null ? "대기 중" : $"{frame.Width}x{frame.Height}";
        Text = $"{_connection.HostName} ({_connection.HostId}) - {size} - {_fps} fps";

        string busy = _qualityLabel.Text ?? "";
        if (_stats is { } s && !busy.StartsWith("보내는", StringComparison.Ordinal) && !busy.StartsWith("받는", StringComparison.Ordinal))
        {
            string quality = s.RttMs < 50 && s.QualityLevel == 0 ? "좋음" : s.RttMs < 150 && s.QualityLevel <= 2 ? "보통" : "나쁨";
            _qualityLabel.Text = $"연결 {quality} · {s.Codec.ToUpperInvariant()} {s.Kbps / 1000.0:F1} Mbps · {s.RttMs} ms · {_connection.TransportKind}";
            _qualityLabel.ToolTipText = s.Encoder is null ? null : $"인코더: {s.Encoder}";
        }
    }

    // ======================= 키보드 =======================

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (_keyboardHook is null)
        {
            try
            {
                _keyboardHook = new KeyboardHook(OnHookKey);
            }
            catch (Win32Exception exception)
            {
                Log.Warn($"키보드 훅 설치 실패. 키보드 입력이 전달되지 않습니다: {exception.Message}");
            }
        }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        // 다른 창으로 전환하면 훅을 제거하고, 눌린 키를 모두 떼서 원격에 키가 눌린 채로 남지 않게 합니다.
        _keyboardHook?.Dispose();
        _keyboardHook = null;
        ReleaseAllInput();
        base.OnDeactivate(e);
    }

    /// <summary>훅 콜백 (UI 스레드). 빨리 끝나야 하므로 큐에 넣기만 합니다.</summary>
    private bool OnHookKey(int virtualKey, bool extended, bool up)
    {
        if (ActiveForm != this || _reconnecting || !_connection.HasFeature(HostFeatures.Input))
        {
            return false;
        }

        if (!up && virtualKey == VkReturn && IsPressed("ControlLeft", "ControlRight") && IsPressed("AltLeft", "AltRight"))
        {
            BeginInvoke(ToggleFullScreen);
            return true;
        }

        if (!KeyCodes.TryGetCode(virtualKey, extended, out string code))
        {
            return false;
        }

        if (up)
        {
            _pressedKeys.Remove(code);
            _input.Enqueue(new KeyUpMessage(code));
        }
        else
        {
            _pressedKeys.Add(code);
            _input.Enqueue(new KeyDownMessage(code));
        }

        return true;
    }

    private bool IsPressed(string left, string right) => _pressedKeys.Contains(left) || _pressedKeys.Contains(right);

    private void TapKeys(params string[] codes)
    {
        foreach (string code in codes)
        {
            _input.Enqueue(new KeyDownMessage(code));
        }

        foreach (string code in codes.Reverse())
        {
            _input.Enqueue(new KeyUpMessage(code));
        }

        _view.Focus();
    }

    private void ReleaseAllInput()
    {
        foreach (string code in _pressedKeys)
        {
            _input.Enqueue(new KeyUpMessage(code));
        }

        foreach (MouseButton button in _pressedButtons)
        {
            _input.Enqueue(new MouseButtonMessage(button, ButtonAction.Up));
        }

        _pressedKeys.Clear();
        _pressedButtons.Clear();
    }

    // ======================= 마우스 =======================

    private void OnViewMouseDown(object? sender, MouseEventArgs e)
    {
        _view.Focus();
        if (TryMapButton(e.Button, out MouseButton button) && SendMouseMove(e.Location))
        {
            _pressedButtons.Add(button);
            _input.Enqueue(new MouseButtonMessage(button, ButtonAction.Down));
        }
    }

    private void OnViewMouseUp(object? sender, MouseEventArgs e)
    {
        if (TryMapButton(e.Button, out MouseButton button) && _pressedButtons.Remove(button))
        {
            SendMouseMove(e.Location);
            _input.Enqueue(new MouseButtonMessage(button, ButtonAction.Up));
        }
    }

    /// <summary>
    /// 화면 좌표 → 원격 화면 0~1 좌표. 검은 여백 위에서는 보내지 않지만,
    /// 버튼을 누른 채 드래그 중이면 가장자리로 고정해서 보냅니다.
    /// </summary>
    private bool SendMouseMove(Point location)
    {
        if (_reconnecting || !_connection.HasFeature(HostFeatures.Input))
        {
            return false;
        }

        Rectangle image = _view.ImageRectangle;
        if (image.IsEmpty || (!image.Contains(location) && _pressedButtons.Count == 0))
        {
            return false;
        }

        double x = Math.Clamp((location.X - image.X + 0.5) / image.Width, 0, 1);
        double y = Math.Clamp((location.Y - image.Y + 0.5) / image.Height, 0, 1);
        _input.Enqueue(new MouseMoveMessage(x, y));
        return true;
    }

    private static bool TryMapButton(MouseButtons buttons, out MouseButton button)
    {
        (bool ok, button) = buttons switch
        {
            MouseButtons.Left => (true, MouseButton.Left),
            MouseButtons.Right => (true, MouseButton.Right),
            MouseButtons.Middle => (true, MouseButton.Middle),
            MouseButtons.XButton1 => (true, MouseButton.X1),
            MouseButtons.XButton2 => (true, MouseButton.X2),
            _ => (false, MouseButton.Left)
        };
        return ok;
    }

    // ======================= 창 =======================

    private void ToggleFullScreen()
    {
        if (FormBorderStyle == FormBorderStyle.None)
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            WindowState = _windowStateBeforeFullScreen;
            _toolbar.Visible = true;
        }
        else
        {
            _windowStateBeforeFullScreen = WindowState;
            _toolbar.Visible = false;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal; // Maximized 상태에서 바로 바꾸면 작업 표시줄이 가려지지 않습니다.
            WindowState = FormWindowState.Maximized;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closing.Cancel();
        _keyboardHook?.Dispose();
        _keyboardHook = null;
        ReleaseAllInput();
        _titleTimer.Dispose();
        Detach(_connection);
        _features.Dispose();
        _input.Dispose();
        _connection.Dispose();
        base.OnFormClosed(e);
    }

    private static Size FitInitialSize(int width, int height)
    {
        Rectangle workArea = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Size maxSize = new((int)(workArea.Width * 0.8), (int)(workArea.Height * 0.8));
        Rectangle fitted = RemoteView.GetImageRectangle(new Size(Math.Max(width, 1), Math.Max(height, 1)), maxSize);
        return fitted.IsEmpty ? new Size(1280, 720) : fitted.Size;
    }
}

/// <summary>Host 공유 폴더의 파일 목록에서 하나를 고르는 창</summary>
internal static class FileListDialog
{
    public static string? Choose(IWin32Window owner, FileListMessage list)
    {
        using var form = new Form
        {
            Text = "원격 PC에서 파일 받기",
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(560, 420),
            MinimizeBox = false,
            MaximizeBox = false
        };

        var view = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false };
        view.Columns.Add("이름", 280);
        view.Columns.Add("크기", 100, HorizontalAlignment.Right);
        view.Columns.Add("수정한 날짜", 140);
        foreach (FileEntry file in list.Files)
        {
            view.Items.Add(new ListViewItem([file.Name, FormatSize(file.Size), file.Modified.LocalDateTime.ToString("yyyy-MM-dd HH:mm")]) { Tag = file.Name });
        }

        var info = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 36,
            Padding = new Padding(6),
            Text = list.Files.Length == 0
                ? $"원격 PC의 공유 폴더가 비어 있습니다.\n({list.Folder})"
                : $"원격 PC 공유 폴더: {list.Folder}   →  내 PC의 다운로드 폴더에 저장됩니다."
        };
        var ok = new Button { Text = "받기", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom, Height = 34 };
        form.Controls.Add(view);
        form.Controls.Add(info);
        form.Controls.Add(ok);
        form.AcceptButton = ok;
        view.DoubleClick += (_, _) => form.DialogResult = DialogResult.OK;

        return form.ShowDialog(owner) == DialogResult.OK && view.SelectedItems.Count == 1
            ? (string)view.SelectedItems[0].Tag!
            : null;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB"
    };
}
