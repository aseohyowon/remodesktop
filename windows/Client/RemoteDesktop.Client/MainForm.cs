using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Win32;
using RemoteDesktop.Core;
using RemoteDesktop.Host.Engine;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.Client;

/// <summary>
/// Remote Desktop 통합 Windows 앱 (STEP 10)
///   [원격 PC에 연결]   등록된 PC 목록, 온라인 상태, LAN/인터넷 연결, 자동 재연결
///   [내 PC 원격 허용]  Host 켜기/끄기, Host ID·접속 코드, 보안 설정, 현재 연결 관리
/// 창을 닫아도 Host가 켜져 있으면 알림 영역(트레이)에서 계속 동작합니다.
/// </summary>
internal sealed class MainForm : Form, IConnectPrompts, IHostCallbacks
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "RemoteDesktop";

    private readonly ClientSettings _settings = ClientSettings.Load();
    private readonly DeviceCredentialStore _devices = new();
    private readonly bool _startInBackground;

    // 연결 탭
    private readonly ListView _hostList = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    private readonly TextBox _signalBox = new() { Dock = DockStyle.Fill, PlaceholderText = "wss://signal.example.com/ws (인터넷 연결용)" };
    private readonly Label _connectStatus = new() { Dock = DockStyle.Fill, AutoSize = false, ForeColor = Color.DimGray, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Dictionary<SavedHost, string> _status = new();

    // Host 탭
    private readonly CheckBox _allowRemote = new() { Text = "내 PC를 원격으로 허용", AutoSize = true, Font = new Font(SystemFonts.DefaultFont.FontFamily, 13, FontStyle.Bold) };
    private readonly Label _hostIdLabel = new() { AutoSize = true, Font = new Font(FontFamily.GenericMonospace, 12, FontStyle.Bold) };
    private readonly Label _accessCodeLabel = new() { AutoSize = true, Font = new Font(FontFamily.GenericMonospace, 12, FontStyle.Bold) };
    private readonly Label _addressLabel = new() { AutoSize = true };
    private readonly Label _internetLabel = new() { AutoSize = true };
    private readonly Label _fingerprintLabel = new() { AutoSize = true, MaximumSize = new Size(520, 0), Font = new Font(FontFamily.GenericMonospace, 8) };
    private readonly Label _sessionLabel = new() { AutoSize = true, ForeColor = Color.DarkGreen };
    private readonly Button _kickButton = new() { Text = "연결 끊기", AutoSize = true, Enabled = false };
    private readonly Label _securityLabel = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly CheckBox _approval = new() { Text = "접속할 때마다 내가 승인 (신뢰된 장치 제외)", AutoSize = true };
    private readonly CheckBox _viewOnly = new() { Text = "화면만 보여 주기 (조작 불가)", AutoSize = true };
    private readonly CheckBox _internet = new() { Text = "인터넷 연결 허용 (시그널링 서버 사용)", AutoSize = true };
    private readonly CheckBox _power = new() { Text = "원격 로그아웃·재시작·종료 허용", AutoSize = true };
    private readonly CheckBox _clipboard = new() { Text = "클립보드 공유", AutoSize = true };
    private readonly CheckBox _files = new() { Text = "파일 전송", AutoSize = true };
    private readonly CheckBox _audio = new() { Text = "소리 전송", AutoSize = true };
    private readonly CheckBox _startup = new() { Text = "Windows 시작 시 자동 실행", AutoSize = true };
    private readonly CheckBox _discoverable = new() { Text = "같은 네트워크에서 이 PC를 찾을 수 있게", AutoSize = true };
    private readonly TextBox _sharedFolder = new() { Width = 300 };

    private readonly NotifyIcon _tray = new() { Text = "Remote Desktop", Icon = SystemIcons.Application };
    private HostServer? _host;
    private CancellationTokenSource? _hostStop;
    private Task _hostTask = Task.CompletedTask;
    private bool _exiting;

    public MainForm(bool startInBackground)
    {
        _startInBackground = startInBackground;
        Text = "Remote Desktop";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(760, 620);
        MinimumSize = new Size(640, 520);
        Icon = SystemIcons.Application;

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildConnectTab());
        tabs.TabPages.Add(BuildHostTab());
        Controls.Add(tabs);

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("열기", null, (_, _) => ShowFromTray());
        trayMenu.Items.Add("원격 허용 켜기/끄기", null, (_, _) => _allowRemote.Checked = !_allowRemote.Checked);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("종료", null, (_, _) => ExitApplication());
        _tray.ContextMenuStrip = trayMenu;
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.Visible = true;

        LoadPreferences();
        ReloadHostList();
        _ = RefreshStatusAsync();
    }

    protected override void SetVisibleCore(bool value)
    {
        // --background로 시작하면 창을 띄우지 않고 트레이에서만 동작
        base.SetVisibleCore(_startInBackground && !IsHandleCreated ? false : value);
        if (_startInBackground && !IsHandleCreated)
        {
            CreateHandle();
        }
    }

    // ================================================================
    // 원격 PC에 연결
    // ================================================================

    private TabPage BuildConnectTab()
    {
        var page = new TabPage("원격 PC에 연결") { Padding = new Padding(10) };
        _hostList.Columns.Add("이름", 170);
        _hostList.Columns.Add("방식", 70);
        _hostList.Columns.Add("주소 / Host ID", 220);
        _hostList.Columns.Add("상태", 90);
        _hostList.DoubleClick += async (_, _) => await ConnectSelectedAsync();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        Button Add(string text, Func<Task> action)
        {
            var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(80, 32) };
            button.Click += async (_, _) => await action();
            buttons.Controls.Add(button);
            return button;
        }

        Add("연결", ConnectSelectedAsync).Font = new Font(Font, FontStyle.Bold);
        Add("같은 네트워크에서 찾기", DiscoverAsync);
        Add("PC 추가", () => { EditHost(null); return Task.CompletedTask; });
        Add("편집", () => { if (Selected is { } h) EditHost(h); return Task.CompletedTask; });
        Add("삭제", () => { DeleteSelected(); return Task.CompletedTask; });
        Add("상태 새로고침", RefreshStatusAsync);

        _signalBox.Text = _settings.SignalingServer ?? "";
        _signalBox.Leave += (_, _) =>
        {
            _settings.SignalingServer = _signalBox.Text.Trim().Length == 0 ? null : _signalBox.Text.Trim();
            _settings.Save();
        };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.Controls.Add(_hostList, 0, 0);
        layout.SetColumnSpan(_hostList, 2);
        layout.Controls.Add(buttons, 0, 1);
        layout.SetColumnSpan(buttons, 2);
        layout.Controls.Add(new Label { Text = "시그널링 서버", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        layout.Controls.Add(_signalBox, 1, 2);
        layout.Controls.Add(_connectStatus, 0, 3);
        layout.SetColumnSpan(_connectStatus, 2);
        page.Controls.Add(layout);
        return page;
    }

    private SavedHost? Selected => _hostList.SelectedItems.Count == 1 ? (SavedHost)_hostList.SelectedItems[0].Tag! : null;

    private void ReloadHostList()
    {
        _hostList.Items.Clear();
        foreach (SavedHost host in _settings.Hosts)
        {
            _hostList.Items.Add(new ListViewItem([
                host.Name,
                host.IsInternet ? "인터넷" : "LAN",
                host.IsInternet ? host.HostId! : $"{host.Address}:{host.Port}{(host.HostId is null ? "" : $"  ({host.HostId})")}",
                _status.GetValueOrDefault(host, "확인 중")
            ]) { Tag = host, ForeColor = _status.GetValueOrDefault(host) == "● Online" ? Color.DarkGreen : SystemColors.WindowText });
        }

        if (_settings.Hosts.Count == 0)
        {
            _connectStatus.Text = "[PC 추가]로 연결할 PC를 등록하세요. 같은 네트워크면 IP 주소, 인터넷이면 Host ID를 입력합니다.";
        }
    }

    private void EditHost(SavedHost? existing)
    {
        using var dialog = new HostEditDialog(existing);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        SavedHost result = dialog.Result;
        if (existing is null)
        {
            _settings.Hosts.Add(result);
        }
        else
        {
            existing.Name = result.Name;
            existing.Address = result.Address;
            existing.Port = result.Port;
            existing.HostId = result.HostId;
        }

        _settings.Save();
        ReloadHostList();
        _ = RefreshStatusAsync();
    }

    private void DeleteSelected()
    {
        if (Selected is not { } host
            || MessageBox.Show(this, $"{host.Name}을(를) 삭제할까요?\n이 PC에 저장된 신뢰 장치 정보도 함께 삭제됩니다.", "삭제", MessageBoxButtons.YesNo) != DialogResult.Yes)
        {
            return;
        }

        _settings.Hosts.Remove(host);
        if (host.HostId is not null)
        {
            _devices.Remove(host.HostId);
        }

        _settings.Save();
        ReloadHostList();
    }

    /// <summary>LAN PC는 포트 열림 확인, 인터넷 PC는 시그널링 서버에 조회</summary>
    private async Task RefreshStatusAsync()
    {
        var lan = _settings.Hosts.Where(h => !h.IsInternet).ToList();
        var internet = _settings.Hosts.Where(h => h.IsInternet).ToList();

        var probes = lan.Select(async host =>
        {
            using var tcp = new TcpClient();
            try
            {
                using var timeout = new CancellationTokenSource(1500);
                await tcp.ConnectAsync(host.Address!, host.Port, timeout.Token);
                _status[host] = "● Online";
            }
            catch
            {
                _status[host] = "○ Offline";
            }
        }).ToList();

        if (internet.Count > 0 && _settings.SignalingUri is { } signal)
        {
            probes.Add(Task.Run(async () =>
            {
                try
                {
                    var builder = new UriBuilder(signal) { Scheme = signal.Scheme == "wss" ? "https" : "http", Path = "/api/status", Query = "ids=" + string.Join(",", internet.Select(h => h.HostId)) };
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                    var result = await http.GetFromJsonAsync<JsonElement>(builder.Uri);
                    var online = result.GetProperty("online");
                    foreach (var host in internet)
                    {
                        _status[host] = online.TryGetProperty(host.HostId!, out var value) && value.GetBoolean() ? "● Online" : "○ Offline";
                    }
                }
                catch
                {
                    foreach (var host in internet)
                    {
                        _status[host] = "? 서버 연결 안 됨";
                    }
                }
            }));
        }
        else
        {
            foreach (var host in internet)
            {
                _status[host] = "? 서버 미설정";
            }
        }

        await Task.WhenAll(probes);
        if (!IsDisposed)
        {
            ReloadHostList();
        }
    }

    private async Task ConnectSelectedAsync()
    {
        if (Selected is not { } host)
        {
            _connectStatus.Text = "목록에서 연결할 PC를 선택하세요.";
            return;
        }

        // 신뢰된 장치면 비밀번호 없이 연결
        LoginRequest? login = host.HostId is not null && _devices.Has(host.HostId)
            ? new LoginRequest(AuthMethods.AccessCode, "", false)
            : AskLogin(host);
        if (login is null)
        {
            return;
        }

        _connectStatus.Text = $"{host.Name}에 연결 중...";
        UseWaitCursor = true;
        try
        {
            RemoteHostConnection connection;
            try
            {
                connection = await ConnectAsync(host, login, CancellationToken.None);
            }
            catch (ConnectionFailedException exception) when (exception.ErrorCode == AuthErrorCodes.DeviceRevoked && login.Secret.Length == 0)
            {
                // 신뢰 장치 등록이 해제됨 → 다시 로그인
                _connectStatus.Text = exception.Message;
                login = AskLogin(host);
                if (login is null)
                {
                    return;
                }

                connection = await ConnectAsync(host, login, CancellationToken.None);
            }

            if (host.HostId != connection.HostId)
            {
                host.HostId = connection.HostId;
                _settings.Save();
                ReloadHostList();
            }

            _connectStatus.Text = $"{host.Name}에 연결됨";
            LoginRequest reconnectLogin = login;
            var viewer = new ViewerForm(connection, token => ConnectAsync(host, reconnectLogin, token));
            viewer.FormClosed += (_, _) =>
            {
                _connectStatus.Text = viewer.CloseReason ?? $"{host.Name} 연결을 종료했습니다.";
                _ = RefreshStatusAsync();
            };
            viewer.Show();
            connection.StartReceiving();
        }
        catch (ConnectionFailedException exception)
        {
            _connectStatus.Text = exception.Message;
            MessageBox.Show(this, exception.Message, "연결 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception exception)
        {
            Log.Error($"Connect failed: {exception}");
            _connectStatus.Text = $"연결 실패: {exception.Message}";
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    /// <summary>같은 네트워크의 Host를 UDP 브로드캐스트로 찾아 목록에 추가합니다 (STEP 11).</summary>
    private async Task DiscoverAsync()
    {
        _connectStatus.Text = "같은 네트워크에서 PC를 찾는 중...";
        UseWaitCursor = true;
        IReadOnlyList<LanDiscovery.FoundHost> found;
        try
        {
            found = await LanDiscovery.DiscoverAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            UseWaitCursor = false;
        }

        string? myHostId = _host?.Settings.HostId;
        var candidates = found
            .Where(f => f.HostId != myHostId)
            .Where(f => !_settings.Hosts.Any(h => h.HostId == f.HostId || (h.Address == f.Address.ToString() && h.Port == f.Port)))
            .ToList();

        if (candidates.Count == 0)
        {
            _connectStatus.Text = found.Count == 0
                ? "찾은 PC가 없습니다. 상대 PC에서 원격 허용이 켜져 있고 같은 네트워크(공유기)에 있는지 확인하세요."
                : "찾은 PC가 모두 이미 목록에 있습니다.";
            return;
        }

        using var dialog = new DiscoveryDialog(candidates);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            _connectStatus.Text = "";
            return;
        }

        foreach (var host in dialog.Selected)
        {
            _settings.Hosts.Add(new SavedHost { Name = host.HostName, Address = host.Address.ToString(), Port = host.Port, HostId = host.HostId });
        }

        _settings.Save();
        ReloadHostList();
        _connectStatus.Text = $"{dialog.Selected.Count}대를 추가했습니다.";
        await RefreshStatusAsync();
    }

    private LoginRequest? AskLogin(SavedHost host)
    {
        using var dialog = new LoginDialog(host.Name);
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.Login : null;
    }

    private async Task<RemoteHostConnection> ConnectAsync(SavedHost host, LoginRequest login, CancellationToken cancellationToken)
    {
        ClientTransport transport;
        if (host.IsInternet)
        {
            Uri signal = _settings.SignalingUri
                ?? throw new ConnectionFailedException("인터넷 연결에는 시그널링 서버 주소가 필요합니다. 아래 칸에 wss://... 주소를 입력하세요.");
            transport = await RemoteHostConnection.ConnectInternetAsync(signal, host.HostId!, cancellationToken);
        }
        else
        {
            transport = await RemoteHostConnection.ConnectLanAsync(host.Address!, host.Port, cancellationToken);
        }

        return await RemoteHostConnection.AuthenticateAsync(transport, login, this, _devices, cancellationToken);
    }

    public bool ConfirmNewHost(NewHostPrompt prompt) => (bool)Invoke(() =>
        MessageBox.Show(this,
            $"처음 연결하는 PC입니다.\n\nHost ID : {prompt.HostId}\nPC 이름 : {prompt.HostName}\n\n인증서 지문:\n{prompt.Fingerprint}\n\n" +
            "상대 PC의 Remote Desktop 화면에 표시된 지문과 같은지 확인하세요.\n같으면 [예]를 누르세요.",
            "새 PC 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes);

    public string? AskTotp(string hostName) =>
        (string?)Invoke(() => InputDialog.Show(this, "2단계 인증", $"{hostName}의 인증 앱에 표시된 6자리 코드를 입력하세요.", numeric: true));

    // ================================================================
    // 내 PC 원격 허용
    // ================================================================

    private TabPage BuildHostTab()
    {
        var page = new TabPage("내 PC 원격 허용") { Padding = new Padding(10), AutoScroll = true };
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        int row = 0;
        void Row(string caption, Control control)
        {
            layout.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 12, 6) }, 0, row);
            layout.Controls.Add(control, 1, row++);
        }

        layout.Controls.Add(_allowRemote, 0, row);
        layout.SetColumnSpan(_allowRemote, 2);
        row++;

        var copyCode = new LinkLabel { Text = "복사", AutoSize = true };
        copyCode.Click += (_, _) => { if (_host is not null) Clipboard.SetText(_host.FormattedAccessCode); };
        var codePanel = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
        codePanel.Controls.Add(_accessCodeLabel);
        codePanel.Controls.Add(copyCode);

        Row("Host ID", _hostIdLabel);
        Row("접속 코드", codePanel);
        Row("LAN 주소", _addressLabel);
        Row("인터넷", _internetLabel);
        Row("인증서 지문", _fingerprintLabel);

        var sessionPanel = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
        sessionPanel.Controls.Add(_sessionLabel);
        sessionPanel.Controls.Add(_kickButton);
        _kickButton.Click += (_, _) => _host?.DisconnectCurrentSession();
        Row("현재 연결", sessionPanel);

        // 보안
        var security = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
        var setPassword = new Button { Text = "비밀번호 설정", AutoSize = true };
        var clearPassword = new Button { Text = "비밀번호 삭제", AutoSize = true };
        var devices = new Button { Text = "신뢰된 장치", AutoSize = true };
        var totp = new Button { Text = "2단계 인증", AutoSize = true };
        var log = new Button { Text = "접속 기록", AutoSize = true };
        security.Controls.AddRange([setPassword, clearPassword, devices, totp, log]);
        Row("보안", security);
        Row("", _securityLabel);

        setPassword.Click += (_, _) =>
        {
            using var dialog = new PasswordDialog();
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                UseWaitCursor = true;
                // 실행 중인 Host와 같은 설정 객체를 써야 바로 적용되고, 서로 덮어쓰지 않습니다.
                (_host?.Settings ?? HostSettings.Load()).SetPassword(dialog.Password);
                UseWaitCursor = false;
                RefreshHostInfo();
            }
        };
        clearPassword.Click += (_, _) =>
        {
            (_host?.Settings ?? HostSettings.Load()).ClearPassword();
            RefreshHostInfo();
        };
        devices.Click += (_, _) =>
        {
            using var dialog = new DevicesDialog(_host?.Settings ?? HostSettings.Load());
            dialog.ShowDialog(this);
            RefreshHostInfo();
        };
        totp.Click += (_, _) =>
        {
            HostSettings settings = _host?.Settings ?? HostSettings.Load();
            if (settings.TotpEnabled)
            {
                if (MessageBox.Show(this, "2단계 인증을 끌까요?", "2단계 인증", MessageBoxButtons.YesNo) == DialogResult.Yes)
                {
                    settings.ClearTotp();
                }
            }
            else
            {
                using var dialog = new TotpDialog(Totp.GenerateSecret(), settings.HostId);
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    settings.SetTotpSecret(dialog.Secret);
                }
            }

            RefreshHostInfo();
        };
        log.Click += (_, _) =>
        {
            using var dialog = new AccessLogDialog();
            dialog.ShowDialog(this);
        };

        // 옵션
        var options = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Margin = Padding.Empty };
        options.Controls.AddRange([_approval, _viewOnly, _clipboard, _files, _audio, _power, _internet, _discoverable, _startup]);
        Row("옵션", options);

        var folderPanel = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
        var browse = new Button { Text = "찾아보기", AutoSize = true };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = _sharedFolder.Text };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _sharedFolder.Text = dialog.SelectedPath;
                SavePreferences(restartHost: true);
            }
        };
        folderPanel.Controls.Add(_sharedFolder);
        folderPanel.Controls.Add(browse);
        Row("파일 공유 폴더", folderPanel);

        page.Controls.Add(layout);

        _allowRemote.CheckedChanged += (_, _) =>
        {
            SavePreferences(restartHost: false);
            if (_allowRemote.Checked)
            {
                StartHost();
            }
            else
            {
                StopHost();
            }
        };

        foreach (CheckBox box in new[] { _approval, _viewOnly, _clipboard, _files, _audio, _power, _internet, _discoverable })
        {
            box.CheckedChanged += (_, _) => SavePreferences(restartHost: true);
        }

        _startup.CheckedChanged += (_, _) =>
        {
            SetStartup(_startup.Checked);
            SavePreferences(restartHost: false);
        };

        return page;
    }

    private void LoadPreferences()
    {
        HostPreferences p = _settings.HostPrefs;
        _loadingPreferences = true; // 체크 상자를 채우는 동안 저장/재시작 이벤트를 막음
        _approval.Checked = p.RequireApproval;
        _viewOnly.Checked = p.ViewOnly;
        _clipboard.Checked = p.AllowClipboard;
        _files.Checked = p.AllowFileTransfer;
        _audio.Checked = p.AllowAudio;
        _power.Checked = p.AllowPower;
        _internet.Checked = p.AllowInternet;
        _discoverable.Checked = p.Discoverable;
        _startup.Checked = p.StartWithWindows;
        _sharedFolder.Text = p.SharedFolder ?? new HostOptions().SharedFolder;
        _loadingPreferences = false;
        _allowRemote.Checked = p.AllowRemote; // 켜져 있었으면 Host 시작
        RefreshHostInfo();
    }

    private bool _loadingPreferences;

    private void SavePreferences(bool restartHost)
    {
        if (_loadingPreferences)
        {
            return;
        }

        HostPreferences p = _settings.HostPrefs;
        p.AllowRemote = _allowRemote.Checked;
        p.RequireApproval = _approval.Checked;
        p.ViewOnly = _viewOnly.Checked;
        p.AllowClipboard = _clipboard.Checked;
        p.AllowFileTransfer = _files.Checked;
        p.AllowAudio = _audio.Checked;
        p.AllowPower = _power.Checked;
        p.AllowInternet = _internet.Checked;
        p.Discoverable = _discoverable.Checked;
        p.StartWithWindows = _startup.Checked;
        p.SharedFolder = _sharedFolder.Text;
        _settings.Save();

        if (restartHost && _host is not null)
        {
            _ = RestartHostAsync();
        }
    }

    /// <summary>옵션을 바꾸면 Host를 다시 시작합니다. 이전 Host가 포트를 놓을 때까지 기다립니다.</summary>
    private async Task RestartHostAsync()
    {
        Task previous = _hostTask;
        StopHost();
        try
        {
            await previous.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch
        {
        }

        if (_allowRemote.Checked)
        {
            StartHost();
        }
    }

    private void StartHost()
    {
        if (_host is not null)
        {
            return;
        }

        HostPreferences p = _settings.HostPrefs;
        Uri? signal = p.AllowInternet ? _settings.SignalingUri : null;
        if (p.AllowInternet && signal is null)
        {
            _internetLabel.Text = "시그널링 서버 주소가 없습니다 ([원격 PC에 연결] 탭 아래에 입력)";
        }

        var options = new HostOptions
        {
            RequireApproval = p.RequireApproval,
            ViewOnly = p.ViewOnly,
            AllowClipboard = p.AllowClipboard,
            AllowFileTransfer = p.AllowFileTransfer,
            AllowAudio = p.AllowAudio,
            AllowPower = p.AllowPower,
            SignalingServer = signal,
            Discoverable = p.Discoverable,
            SharedFolder = string.IsNullOrWhiteSpace(p.SharedFolder) ? new HostOptions().SharedFolder : p.SharedFolder
        };

        try
        {
            _host = new HostServer(options, HostSettings.Load(), this);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"원격 허용을 시작할 수 없습니다: {exception.Message}", Text);
            _allowRemote.Checked = false;
            return;
        }

        _hostStop = new CancellationTokenSource();
        HostServer host = _host;
        CancellationToken token = _hostStop.Token;
        _hostTask = Task.Run(async () =>
        {
            try
            {
                await host.RunAsync(token);
            }
            catch (Exception exception) when (!token.IsCancellationRequested)
            {
                Log.Error($"Host stopped: {exception.Message}");
                BeginInvoke(() =>
                {
                    MessageBox.Show(this, $"원격 허용이 중지되었습니다: {exception.Message}\n(다른 Host 프로그램이 같은 포트를 쓰고 있는지 확인하세요)", Text);
                    _allowRemote.Checked = false;
                });
            }
        });

        _tray.Text = $"Remote Desktop - 원격 허용 중 ({host.Settings.HostId})";
        RefreshHostInfo();
    }

    private void StopHost()
    {
        _hostStop?.Cancel();
        _hostStop = null;
        _host = null;
        _tray.Text = "Remote Desktop";
        RefreshHostInfo();
    }

    private void RefreshHostInfo()
    {
        HostServer? host = _host;
        HostSettings settings = host?.Settings ?? HostSettings.Load();
        _hostIdLabel.Text = settings.HostId;
        _accessCodeLabel.Text = host is null ? "-" : host.Options.AccessCodeEnabled ? host.FormattedAccessCode : "사용 안 함";
        _addressLabel.Text = host is null ? "꺼짐" : string.Join(", ", HostServer.GetLanAddresses().Select(a => $"{a}:{host.Options.Port}"));
        _internetLabel.Text = host?.Options.SignalingServer is { } signal ? $"{signal.Host}에 {settings.HostId}로 등록" : "사용 안 함";
        _fingerprintLabel.Text = host is null ? "-" : AuthProof.FormatFingerprint(host.TlsChannelBinding);
        _securityLabel.Text = $"비밀번호 {(settings.HasPassword ? "설정됨" : "없음")} · 2단계 인증 {(settings.TotpEnabled ? "켜짐" : "꺼짐")} · 신뢰된 장치 {settings.ListDevices().Count}개";
        if (host is null)
        {
            _sessionLabel.Text = "-";
            _kickButton.Enabled = false;
        }
        else if (!host.HasActiveSession)
        {
            _sessionLabel.Text = "대기 중";
            _kickButton.Enabled = false;
        }
    }

    // IHostCallbacks (Host 엔진 스레드에서 호출)

    public async Task<bool> ApproveAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(() =>
        {
            _tray.ShowBalloonTip(5000, "원격 접속 요청", $"{request.ClientName}이(가) 접속하려고 합니다.", ToolTipIcon.Info);
            using var dialog = new ApprovalDialog(request);
            completion.TrySetResult(dialog.ShowDialog() == DialogResult.OK);
        });
        using (cancellationToken.Register(() => completion.TrySetResult(false)))
        {
            return await completion.Task;
        }
    }

    public void OnSessionStarted(SessionInfo session)
    {
        BeginInvoke(() =>
        {
            _sessionLabel.Text = $"{session.ClientName} ({session.Platform}, {session.Transport}, {session.Remote})";
            _kickButton.Enabled = true;
            _tray.ShowBalloonTip(5000, "원격 연결됨", $"{session.ClientName}이(가) 이 PC에 연결했습니다.", ToolTipIcon.Warning);
        });
    }

    public void OnSessionEnded(SessionInfo session, string reason)
    {
        BeginInvoke(() =>
        {
            _sessionLabel.Text = "대기 중";
            _kickButton.Enabled = false;
            _tray.ShowBalloonTip(3000, "원격 연결 종료", $"{session.ClientName}: {reason}", ToolTipIcon.Info);
        });
    }

    // ================================================================
    // 창 / 트레이 / 자동 실행
    // ================================================================

    private static void SetStartup(bool enabled)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null)
        {
            return;
        }

        if (enabled)
        {
            key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --background");
        }
        else
        {
            key.DeleteValue(RunValue, throwOnMissingValue: false);
        }
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 원격 허용 중에는 창을 닫아도 트레이에서 계속 동작
        if (!_exiting && e.CloseReason == CloseReason.UserClosing && _host is not null && _settings.HostPrefs.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloonTip(3000, "Remote Desktop", "원격 허용이 켜져 있어 알림 영역에서 계속 실행됩니다.", ToolTipIcon.Info);
            return;
        }

        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        StopHost();
        _tray.Visible = false;
        _tray.Dispose();
        base.OnFormClosed(e);
    }
}
