using System.Drawing.Drawing2D;

namespace RemoteDesktop.Client;

/// <summary>
/// 원격 화면을 그리는 컨트롤. 원격 화면 비율을 유지하며 영역에 맞춰 확대/축소합니다.
/// 마우스 이벤트는 ViewerForm이 받아 0~1 좌표로 바꿔 보냅니다.
/// </summary>
internal sealed class RemoteView : Control
{
    private const int WmMouseHorizontalWheel = 0x020E;
    private Bitmap? _frame;
    private string? _overlay;

    public RemoteView()
    {
        BackColor = Color.Black;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable,
            true);
        AllowDrop = true;
    }

    /// <summary>가로 휠 (WM_MOUSEHWHEEL). 양수 = 오른쪽</summary>
    public event Action<int>? HorizontalWheel;

    /// <summary>원격 화면 크기 (프레임이 아직 없으면 Host가 알려 준 크기)</summary>
    public Size RemoteSize { get; set; }

    public Bitmap? Frame => _frame;

    /// <summary>새 프레임으로 바꾸고 이전 프레임은 해제합니다 (UI 스레드).</summary>
    public void SetFrame(Bitmap frame)
    {
        Bitmap? previous = _frame;
        _frame = frame;
        previous?.Dispose();
        Invalidate();
    }

    /// <summary>화면 가운데 안내 문구 (예: "다시 연결 중..."). null이면 숨김</summary>
    public string? Overlay
    {
        get => _overlay;
        set
        {
            _overlay = value;
            Invalidate();
        }
    }

    public Rectangle ImageRectangle => GetImageRectangle(_frame?.Size ?? RemoteSize, ClientSize);

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics graphics = e.Graphics;
        graphics.Clear(Color.Black);

        if (_frame is not null)
        {
            Rectangle target = ImageRectangle;
            bool exactSize = target.Width == _frame.Width && target.Height == _frame.Height;
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = exactSize ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(_frame, target);
            graphics.CompositingMode = CompositingMode.SourceOver;
        }

        string? text = _overlay ?? (_frame is null ? "화면을 기다리는 중..." : null);
        if (text is not null)
        {
            using var shade = new SolidBrush(Color.FromArgb(_frame is null ? 0 : 160, 0, 0, 0));
            graphics.FillRectangle(shade, ClientRectangle);
            TextRenderer.DrawText(graphics, text, new Font(Font.FontFamily, 12), ClientRectangle, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmMouseHorizontalWheel)
        {
            HorizontalWheel?.Invoke((short)((m.WParam.ToInt64() >> 16) & 0xFFFF));
            m.Result = IntPtr.Zero;
            return;
        }

        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _frame?.Dispose();
            _frame = null;
        }

        base.Dispose(disposing);
    }

    /// <summary>원격 화면 비율을 유지한 채 영역 안에 들어가는 가장 큰 사각형 (가운데 정렬).</summary>
    internal static Rectangle GetImageRectangle(Size image, Size area)
    {
        if (image.Width <= 0 || image.Height <= 0 || area.Width <= 0 || area.Height <= 0)
        {
            return Rectangle.Empty;
        }

        double scale = Math.Min((double)area.Width / image.Width, (double)area.Height / image.Height);
        int width = (int)Math.Round(image.Width * scale);
        int height = (int)Math.Round(image.Height * scale);
        return new Rectangle((area.Width - width) / 2, (area.Height - height) / 2, width, height);
    }
}
