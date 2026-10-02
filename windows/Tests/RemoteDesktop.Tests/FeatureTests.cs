using System.Security.Cryptography;
using RemoteDesktop.Client;
using RemoteDesktop.Host.Engine;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.Tests;

/// <summary>실제 클립보드 대신 쓰는 가짜</summary>
internal sealed class FakeClipboard : IClipboardAccess
{
    private readonly object _sync = new();
    private string? _text;

    public uint SequenceNumber { get; private set; } = 1;

    public string? Text
    {
        get
        {
            lock (_sync) return _text;
        }
    }

    public string? GetText()
    {
        lock (_sync) return _text;
    }

    public void SetText(string text)
    {
        lock (_sync)
        {
            _text = text;
            SequenceNumber++;
        }
    }

    /// <summary>사용자가 이 PC에서 복사한 것처럼</summary>
    public void UserCopies(string text) => SetText(text);
}

internal sealed class FakePower : IPowerController
{
    public List<string> Executed { get; } = new();

    public void Execute(string action) => Executed.Add(action);
}

/// <summary>STEP 9: 클립보드, 파일 전송, 전원, 오디오 (실제 Host 엔진 + Windows Client 코드)</summary>
public sealed class FeatureTests : IAsyncLifetime
{
    private readonly string _shared = Directory.CreateTempSubdirectory("rd-shared-").FullName;
    private readonly string _downloads = Directory.CreateTempSubdirectory("rd-downloads-").FullName;
    private readonly FakeClipboard _hostClipboard = new();
    private readonly FakePower _power = new();
    private TestHost _host = null!;
    private RemoteHostConnection _connection = null!;
    private ClientFeatures _features = null!;

    private async Task StartAsync(HostOptions? options = null)
    {
        _host = new TestHost((options ?? new HostOptions()) with { SharedFolder = _shared, Codec = "jpeg" }, viewOnly: false);
        _host.Server.ClipboardFactory = () => _hostClipboard;
        _host.Server.PowerController = _power;
        _connection = await _host.ConnectAsync(new LoginRequest(AuthMethods.AccessCode, _host.Server.AccessCode, false));
        _features = new ClientFeatures(_connection);
        _connection.FrameReceived += (bitmap, header) =>
        {
            bitmap.Dispose();
            _ = _connection.SendAckAsync(header.FrameId);
        };
        _connection.StartReceiving();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _features?.Dispose();
        _connection?.Dispose();
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        for (int i = 0; i < 50 && !condition(); i++)
        {
            await Task.Delay(100);
        }

        Assert.True(condition(), what);
    }

    [Fact]
    public async Task ClipboardBothWaysWithoutEcho()
    {
        await StartAsync();
        var clientClipboard = new FakeClipboard();
        _features.SetClipboardSync(true, clientClipboard);
        var fromHost = new List<string>();
        _features.RemoteClipboardReceived += t => { lock (fromHost) fromHost.Add(t); };
        await Task.Delay(600);

        // Client에서 복사 → Host 클립보드
        clientClipboard.UserCopies("PC A에서 복사한 글자");
        await WaitUntil(() => _hostClipboard.Text == "PC A에서 복사한 글자", "Host 클립보드에 들어가야 함");

        // Host에서 복사 → Client 클립보드
        _hostClipboard.UserCopies("PC B에서 복사");
        await WaitUntil(() => clientClipboard.Text == "PC B에서 복사", "Client 클립보드에 들어가야 함");

        // 되돌려 보내기(echo)가 없어야 함: Host가 보낸 것은 "PC B에서 복사" 하나뿐
        await Task.Delay(1000);
        lock (fromHost)
        {
            Assert.Equal(["PC B에서 복사"], fromHost);
        }
    }

    [Fact]
    public async Task UploadAndDownloadWithIntegrityCheck()
    {
        await StartAsync();
        string source = Path.Combine(_downloads, "upload source.bin");
        byte[] content = RandomNumberGenerator.GetBytes(300_000); // 64 KB 조각 여러 개
        await File.WriteAllBytesAsync(source, content);

        // 올리기 → Host 공유 폴더\Received
        long lastProgress = 0;
        FileResultMessage up = await _features.UploadAsync(source, new Progress<long>(p => lastProgress = p), CancellationToken.None);
        Assert.True(up.Success, up.Error);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(_shared, "Received", up.SavedAs!)));

        // 같은 이름으로 한 번 더 → 덮어쓰지 않고 "(1)" 붙음
        FileResultMessage again = await _features.UploadAsync(source, null, CancellationToken.None);
        Assert.Equal("upload source (1).bin", again.SavedAs);

        // 목록 + 받기
        await File.WriteAllTextAsync(Path.Combine(_shared, "report.txt"), "보고서 내용");
        FileListMessage list = await _features.GetFileListAsync(CancellationToken.None);
        Assert.Contains(list.Files, f => f.Name == "report.txt");

        FileResultMessage down = await _features.DownloadAsync("report.txt", _downloads, CancellationToken.None);
        Assert.True(down.Success, down.Error);
        Assert.Equal("보고서 내용", await File.ReadAllTextAsync(Path.Combine(_downloads, down.SavedAs!)));
    }

    [Theory]
    [InlineData("..\\host.json")]
    [InlineData("../host.json")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("Received")]
    public async Task DownloadOutsideSharedFolderIsRefused(string name)
    {
        await StartAsync();
        Directory.CreateDirectory(Path.Combine(_shared, "Received"));
        FileResultMessage result = await _features.DownloadAsync(name, _downloads, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Empty(Directory.GetFiles(_downloads));
    }

    [Theory]
    [InlineData("..\\evil.txt")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("a/b.txt")]
    public void DangerousUploadNamesAreRejected(string name)
    {
        using var receiver = new FileReceiver(_shared);
        FileResultMessage? result = receiver.Begin(new FileBeginMessage(1, name, 10, "upload"));
        Assert.NotNull(result);
        Assert.False(result!.Success);
    }

    [Fact]
    public void CorruptedUploadIsDiscarded()
    {
        using var receiver = new FileReceiver(_shared);
        Assert.Null(receiver.Begin(new FileBeginMessage(7, "data.bin", 4, "upload")));
        byte[] chunk = new byte[12 + 4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(chunk, 7);
        Assert.Null(receiver.Chunk(chunk));
        FileResultMessage result = receiver.End(new FileEndMessage(7, new string('0', 64)));
        Assert.False(result.Success);
        Assert.Empty(Directory.GetFiles(_shared, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PowerActionsNeedPermission()
    {
        await StartAsync();

        PowerResultMessage locked = await _features.PowerAsync("lock", CancellationToken.None);
        Assert.True(locked.Success);

        PowerResultMessage restart = await _features.PowerAsync("restart", CancellationToken.None);
        Assert.False(restart.Success); // --allow-power 없음
        Assert.Equal(["lock"], _power.Executed);
    }

    [Fact]
    public async Task PowerActionsAllowedWithOption()
    {
        await StartAsync(new HostOptions { AllowPower = true });
        PowerResultMessage restart = await _features.PowerAsync("restart", CancellationToken.None);
        Assert.True(restart.Success);
        await WaitUntil(() => _power.Executed.Contains("restart"), "재시작이 실행되어야 함");
    }

    [Fact]
    public async Task AudioStartSendsFormat()
    {
        await StartAsync();
        var format = new TaskCompletionSource<AudioFormatMessage>();
        _connection.ControlReceived += m =>
        {
            if (m is AudioFormatMessage f) format.TrySetResult(f);
        };

        await _features.SetAudioAsync(true);
        AudioFormatMessage received = await format.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("pcm16", received.Codec);
        Assert.True(received.SampleRate >= 8000);
        await _features.SetAudioAsync(false);
    }

    [Fact]
    public async Task FeaturesAreOffInViewOnlyMode()
    {
        _host = new TestHost(new HostOptions { SharedFolder = _shared, Codec = "jpeg", AllowPower = true }, viewOnly: true);
        _host.Server.PowerController = _power;
        _connection = await _host.ConnectAsync(new LoginRequest(AuthMethods.AccessCode, _host.Server.AccessCode, false));
        _features = new ClientFeatures(_connection);
        _connection.StartReceiving();

        Assert.DoesNotContain(HostFeatures.Input, _connection.Features);
        Assert.DoesNotContain(HostFeatures.FileTransfer, _connection.Features);
        PowerResultMessage result = await _features.PowerAsync("lock", CancellationToken.None);
        Assert.False(result.Success);
        Assert.Empty(_power.Executed);
    }
}
