using RemoteDesktop.Host.Engine;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.Client;

/// <summary>대화 상자 공통 모양</summary>
internal abstract class DialogBase : Form
{
    protected DialogBase(string title)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        ShowInTaskbar = false;
    }

    protected Button[] AddButtons(TableLayoutPanel layout, string okText = "확인")
    {
        var ok = new Button { Text = okText, DialogResult = DialogResult.OK, AutoSize = true, MinimumSize = new Size(80, 30) };
        var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(80, 30) };
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons, 0, layout.RowCount);
        layout.SetColumnSpan(buttons, Math.Max(1, layout.ColumnCount));
        AcceptButton = ok;
        CancelButton = cancel;
        return [ok, cancel];
    }

    protected static TableLayoutPanel NewLayout(int columns = 2) =>
        new() { ColumnCount = columns, AutoSize = true, Dock = DockStyle.Fill };

    protected static Label Caption(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) };
}

/// <summary>한 줄 입력 대화 상자</summary>
internal static class InputDialog
{
    public static string? Show(IWin32Window owner, string title, string message, bool numeric = false, bool password = false)
    {
        using var form = new SimpleInput(title, message, numeric, password);
        return form.ShowDialog(owner) == DialogResult.OK ? form.Value : null;
    }

    private sealed class SimpleInput : DialogBase
    {
        private readonly TextBox _box;

        public SimpleInput(string title, string message, bool numeric, bool password) : base(title)
        {
            var layout = NewLayout(1);
            layout.Controls.Add(new Label { Text = message, AutoSize = true, MaximumSize = new Size(360, 0) });
            _box = new TextBox { Width = 240, UseSystemPasswordChar = password, MaxLength = numeric ? 6 : 256 };
            if (numeric)
            {
                _box.KeyPress += (_, e) => e.Handled = !char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar);
            }

            layout.Controls.Add(_box);
            AddButtons(layout);
            Controls.Add(layout);
        }

        public string Value => _box.Text;
    }
}

/// <summary>로그인: 접속 코드 / 비밀번호 + "이 PC 기억하기"</summary>
internal sealed class LoginDialog : DialogBase
{
    private readonly RadioButton _code = new() { Text = "접속 코드", AutoSize = true, Checked = true };
    private readonly RadioButton _password = new() { Text = "비밀번호", AutoSize = true };
    private readonly TextBox _secret = new() { Width = 240, UseSystemPasswordChar = true };
    private readonly CheckBox _remember = new() { Text = "이 PC 기억하기 (다음부터 비밀번호 없이 연결)", AutoSize = true };

    public LoginDialog(string hostName) : base($"{hostName} 연결")
    {
        var layout = NewLayout();
        var methods = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
        methods.Controls.Add(_code);
        methods.Controls.Add(_password);
        layout.Controls.Add(Caption("로그인 방식"), 0, 0);
        layout.Controls.Add(methods, 1, 0);
        layout.Controls.Add(Caption("코드 / 비밀번호"), 0, 1);
        layout.Controls.Add(_secret, 1, 1);
        layout.Controls.Add(_remember, 1, 2);
        layout.RowCount = 3;
        AddButtons(layout, "연결");
        Controls.Add(layout);
    }

    public LoginRequest Login => new(_password.Checked ? AuthMethods.Password : AuthMethods.AccessCode, _secret.Text, _remember.Checked);
}

/// <summary>PC 추가/편집: 같은 네트워크(IP) 또는 인터넷(Host ID)</summary>
internal sealed class HostEditDialog : DialogBase
{
    private readonly TextBox _name = new() { Width = 240 };
    private readonly RadioButton _lan = new() { Text = "같은 네트워크 (IP)", AutoSize = true, Checked = true };
    private readonly RadioButton _internet = new() { Text = "인터넷 (Host ID)", AutoSize = true };
    private readonly TextBox _address = new() { Width = 240, PlaceholderText = "192.168.0.10" };
    private readonly NumericUpDown _port = new() { Width = 100, Minimum = 1024, Maximum = 65535, Value = ProtocolConstants.DefaultPort };
    private readonly TextBox _hostId = new() { Width = 240, PlaceholderText = "HOST-8F29A1", CharacterCasing = CharacterCasing.Upper };

    public HostEditDialog(SavedHost? existing) : base(existing is null ? "PC 추가" : "PC 편집")
    {
        var layout = NewLayout();
        var mode = new FlowLayoutPanel { AutoSize = true, Margin = Padding.Empty };
        mode.Controls.Add(_lan);
        mode.Controls.Add(_internet);
        layout.Controls.Add(Caption("이름"), 0, 0);
        layout.Controls.Add(_name, 1, 0);
        layout.Controls.Add(Caption("연결 방식"), 0, 1);
        layout.Controls.Add(mode, 1, 1);
        layout.Controls.Add(Caption("주소 (IP)"), 0, 2);
        layout.Controls.Add(_address, 1, 2);
        layout.Controls.Add(Caption("포트"), 0, 3);
        layout.Controls.Add(_port, 1, 3);
        layout.Controls.Add(Caption("Host ID"), 0, 4);
        layout.Controls.Add(_hostId, 1, 4);
        layout.RowCount = 5;
        Button ok = AddButtons(layout, "저장")[0];
        Controls.Add(layout);

        if (existing is not null)
        {
            _name.Text = existing.Name;
            _internet.Checked = existing.IsInternet;
            _address.Text = existing.Address ?? "";
            _port.Value = existing.Port;
            _hostId.Text = existing.HostId ?? "";
        }

        void Update()
        {
            _address.Enabled = _port.Enabled = _lan.Checked;
            _hostId.Enabled = _internet.Checked;
        }

        _lan.CheckedChanged += (_, _) => Update();
        Update();

        ok.Click += (_, e) =>
        {
            bool valid = _name.Text.Trim().Length > 0
                && (_lan.Checked ? _address.Text.Trim().Length > 0 : System.Text.RegularExpressions.Regex.IsMatch(_hostId.Text.Trim(), "^[A-Z0-9-]{6,40}$"));
            if (!valid)
            {
                MessageBox.Show(this, _lan.Checked ? "이름과 IP 주소를 입력하세요." : "이름과 Host ID(예: HOST-8F29A1)를 입력하세요.", Text);
                DialogResult = DialogResult.None;
            }
        };
    }

    public SavedHost Result => new()
    {
        Name = _name.Text.Trim(),
        Address = _lan.Checked ? _address.Text.Trim() : null,
        Port = (int)_port.Value,
        HostId = _hostId.Text.Trim().Length > 0 ? _hostId.Text.Trim() : null
    };
}

/// <summary>접속 비밀번호 설정</summary>
internal sealed class PasswordDialog : DialogBase
{
    private readonly TextBox _first = new() { Width = 220, UseSystemPasswordChar = true };
    private readonly TextBox _second = new() { Width = 220, UseSystemPasswordChar = true };

    public PasswordDialog() : base("접속 비밀번호 설정")
    {
        var layout = NewLayout();
        layout.Controls.Add(Caption($"새 비밀번호 ({HostSettings.MinPasswordLength}자 이상)"), 0, 0);
        layout.Controls.Add(_first, 1, 0);
        layout.Controls.Add(Caption("한 번 더"), 0, 1);
        layout.Controls.Add(_second, 1, 1);
        layout.RowCount = 2;
        Button ok = AddButtons(layout)[0];
        Controls.Add(layout);
        ok.Click += (_, _) =>
        {
            if (_first.Text.Length < HostSettings.MinPasswordLength || _first.Text != _second.Text)
            {
                MessageBox.Show(this, _first.Text != _second.Text ? "두 비밀번호가 다릅니다." : $"{HostSettings.MinPasswordLength}자 이상 입력하세요.", Text);
                DialogResult = DialogResult.None;
            }
        };
    }

    public string Password => _first.Text;
}

/// <summary>2단계 인증(TOTP) 설정: 인증 앱에 키 등록 → 코드 확인</summary>
internal sealed class TotpDialog : DialogBase
{
    private readonly TextBox _code = new() { Width = 120, MaxLength = 6 };

    public TotpDialog(byte[] secret, string hostId) : base("2단계 인증 설정")
    {
        Secret = secret;
        var layout = NewLayout(1);
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            MaximumSize = new Size(460, 0),
            Text = "1) 휴대폰 인증 앱(Google Authenticator, Microsoft Authenticator 등)에서 '키 입력'으로 아래 키를 등록하세요.\n" +
                   "2) 앱에 표시된 6자리 코드를 입력하세요."
        });
        layout.Controls.Add(new TextBox { Text = Totp.ToBase32(secret), ReadOnly = true, Width = 460, Font = new Font(FontFamily.GenericMonospace, 11) });
        layout.Controls.Add(new TextBox { Text = Totp.ToOtpAuthUri(secret, hostId), ReadOnly = true, Width = 460 });
        layout.Controls.Add(_code);
        layout.RowCount = 4;
        Button ok = AddButtons(layout, "켜기")[0];
        Controls.Add(layout);
        ok.Click += (_, _) =>
        {
            if (!new Totp(secret).Verify(_code.Text.Trim(), DateTimeOffset.UtcNow))
            {
                MessageBox.Show(this, "코드가 맞지 않습니다. PC와 휴대폰의 시계가 맞는지 확인하세요.", Text);
                DialogResult = DialogResult.None;
            }
        };
    }

    public byte[] Secret { get; }
}

/// <summary>신뢰된 장치 목록과 등록 해제</summary>
internal sealed class DevicesDialog : DialogBase
{
    public DevicesDialog(HostSettings settings) : base("신뢰된 장치")
    {
        AutoSize = false;
        Size = new Size(620, 380);
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
        list.Columns.Add("ID", 120);
        list.Columns.Add("이름", 170);
        list.Columns.Add("등록", 120);
        list.Columns.Add("마지막 사용", 120);
        list.Columns.Add("상태", 60);

        void Reload()
        {
            list.Items.Clear();
            foreach (var device in settings.ListDevices())
            {
                list.Items.Add(new ListViewItem([device.Id, device.Name, device.Created.LocalDateTime.ToString("yyyy-MM-dd HH:mm"),
                    device.LastUsed.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), device.Expired ? "만료" : "사용 중"]) { Tag = device.Id });
            }
        }

        var revoke = new Button { Text = "선택한 장치 등록 해제", Dock = DockStyle.Bottom, Height = 32 };
        var revokeAll = new Button { Text = "모두 해제", Dock = DockStyle.Bottom, Height = 32 };
        revoke.Click += (_, _) =>
        {
            foreach (ListViewItem item in list.SelectedItems)
            {
                settings.RevokeDevice((string)item.Tag!);
            }

            Reload();
        };
        revokeAll.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "모든 신뢰된 장치의 등록을 해제할까요?\n해당 장치는 다시 로그인해야 합니다.", Text, MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                settings.RevokeDevice("all");
                Reload();
            }
        };

        Controls.Add(list);
        Controls.Add(revoke);
        Controls.Add(revokeAll);
        Reload();
    }
}

/// <summary>접속 기록 보기</summary>
internal sealed class AccessLogDialog : DialogBase
{
    public AccessLogDialog() : base("접속 기록")
    {
        AutoSize = false;
        Size = new Size(820, 480);
        var text = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9),
            Text = string.Join(Environment.NewLine, AccessLog.ReadLast(300).Reverse())
        };
        Controls.Add(text);
        Controls.Add(new Label { Dock = DockStyle.Top, Text = $"최근 기록이 위에 있습니다. 파일: {AccessLog.FilePath}", Height = 24 });
    }
}

/// <summary>접속 승인 요청 (30초 후 자동 거부)</summary>
internal sealed class ApprovalDialog : DialogBase
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private int _remaining = 30;

    public ApprovalDialog(ApprovalRequest request) : base("원격 접속 요청")
    {
        TopMost = true;
        StartPosition = FormStartPosition.CenterScreen;
        var layout = NewLayout(1);
        var message = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(420, 0),
            Font = new Font(Font.FontFamily, 10),
            Text = $"{request.ClientName} ({request.Platform})이(가) 이 PC에 원격으로 접속하려고 합니다.\n\n" +
                   $"주소: {request.Remote}\n인증: {request.Method}\n\n허용하면 상대가 이 PC의 화면을 보고 조작할 수 있습니다."
        };
        layout.Controls.Add(message);
        layout.RowCount = 1;
        Button[] buttons = AddButtons(layout, "허용");
        buttons[1].Text = "거부";
        Controls.Add(layout);
        AcceptButton = null; // 실수로 Enter로 허용하지 않도록

        _timer.Tick += (_, _) =>
        {
            _remaining--;
            buttons[1].Text = $"거부 ({_remaining})";
            if (_remaining <= 0)
            {
                DialogResult = DialogResult.Cancel;
            }
        };
        _timer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        _timer.Dispose();
        base.Dispose(disposing);
    }
}
