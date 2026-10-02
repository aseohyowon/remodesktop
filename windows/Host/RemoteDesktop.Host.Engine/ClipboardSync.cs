using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using RemoteDesktop.Core;

namespace RemoteDesktop.Host.Engine;

/// <summary>클립보드 접근 (테스트에서 실제 클립보드를 건드리지 않도록 분리)</summary>
public interface IClipboardAccess
{
    /// <summary>클립보드가 바뀔 때마다 증가하는 번호</summary>
    uint SequenceNumber { get; }

    string? GetText();

    void SetText(string text);
}

public sealed class WindowsClipboard : IClipboardAccess
{
    public uint SequenceNumber => GetClipboardSequenceNumber();

    public string? GetText() => Clipboard.ContainsText() ? Clipboard.GetText() : null;

    public void SetText(string text) => Clipboard.SetText(text);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}

/// <summary>
/// 텍스트 클립보드 동기화 (양방향)
/// - 이 PC에서 복사하면 TextChanged 이벤트 → 상대에게 전송
/// - 상대가 보낸 텍스트는 SetRemoteText로 이 PC 클립보드에 넣음 (다시 되돌려 보내지 않음)
/// Windows 클립보드는 STA 스레드에서만 다룰 수 있어 전용 스레드에서 0.4초마다 확인합니다.
/// </summary>
public sealed class ClipboardSync : IDisposable
{
    public const int MaxLength = 32_000;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(400);

    private readonly IClipboardAccess _clipboard;
    private readonly BlockingCollection<string> _incoming = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private uint _lastSequence;
    private string? _lastRemote;

    public ClipboardSync(IClipboardAccess? clipboard = null)
    {
        _clipboard = clipboard ?? new WindowsClipboard();
        _thread = new Thread(Run) { IsBackground = true, Name = "clipboard-sync" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>이 PC의 클립보드 텍스트가 사용자에 의해 바뀜 (전용 스레드에서 호출)</summary>
    public event Action<string>? TextChanged;

    public void SetRemoteText(string text)
    {
        if (text.Length <= MaxLength && !_incoming.IsAddingCompleted)
        {
            try
            {
                _incoming.Add(text);
            }
            catch (InvalidOperationException)
            {
                // 이미 종료됨
            }
        }
    }

    private void Run()
    {
        _lastSequence = Safe(() => _clipboard.SequenceNumber);
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (_incoming.TryTake(out string? remote, PollInterval))
                {
                    Retry(() => _clipboard.SetText(remote));
                    _lastRemote = remote;
                    _lastSequence = _clipboard.SequenceNumber; // 방금 넣은 변경은 무시
                    continue;
                }

                uint sequence = _clipboard.SequenceNumber;
                if (sequence == _lastSequence)
                {
                    continue;
                }

                _lastSequence = sequence;
                string? text = Retry(() => _clipboard.GetText());
                if (text is not null && text != _lastRemote)
                {
                    TextChanged?.Invoke(text.Length > MaxLength ? text[..MaxLength] : text);
                }
            }
            catch (Exception exception) when (exception is ExternalException or ThreadInterruptedException or InvalidOperationException)
            {
                Log.Debug($"Clipboard: {exception.Message}");
            }
        }
    }

    /// <summary>다른 프로그램이 클립보드를 잠깐 쓰고 있으면 실패하므로 몇 번 다시 시도합니다.</summary>
    private static T Retry<T>(Func<T> action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (ExternalException) when (attempt < 5)
            {
                Thread.Sleep(30);
            }
        }
    }

    private static void Retry(Action action) => Retry(() =>
    {
        action();
        return 0;
    });

    private static uint Safe(Func<uint> action)
    {
        try
        {
            return action();
        }
        catch (ExternalException)
        {
            return 0;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _incoming.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(1));
    }
}
