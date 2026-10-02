using System.Collections.Concurrent;
using System.Buffers.Binary;
using NAudio.Wave;
using RemoteDesktop.Core;
using RemoteDesktop.Host.Engine;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.Client;

/// <summary>
/// Windows Client 쪽 STEP 9 기능: 파일 보내기/받기, 클립보드 동기화, 소리 재생, 전원 명령.
/// RemoteHostConnection의 제어/바이너리 이벤트를 받아 처리합니다.
/// </summary>
public sealed class ClientFeatures : IDisposable
{
    private readonly RemoteHostConnection _connection;
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<FileResultMessage>> _uploads = new();
    private readonly ConcurrentDictionary<uint, TaskCompletionSource<FileResultMessage>> _downloads = new();
    private TaskCompletionSource<FileListMessage>? _fileList;
    private TaskCompletionSource<PowerResultMessage>? _power;
    private TaskCompletionSource<DisplayModesMessage>? _displayModes;
    private TaskCompletionSource<DisplayResultMessage>? _displayResult;
    private FileReceiver? _downloadReceiver;
    private string? _downloadFolder;
    private ClipboardSync? _clipboard;
    private AudioPlayer? _audio;
    private int _nextUploadId = 1_000_000; // Host가 쓰는 번호와 겹치지 않게

    public ClientFeatures(RemoteHostConnection connection)
    {
        _connection = connection;
        connection.ControlReceived += OnControl;
        connection.BinaryReceived += OnBinary;
    }

    /// <summary>Host가 클립보드를 보내왔을 때 (UI 알림용)</summary>
    public event Action<string>? RemoteClipboardReceived;

    // ---------------- 클립보드 ----------------

    public bool ClipboardEnabled => _clipboard is not null;

    public void SetClipboardSync(bool enabled, IClipboardAccess? clipboard = null)
    {
        if (enabled && _clipboard is null && _connection.HasFeature(HostFeatures.Clipboard))
        {
            _clipboard = new ClipboardSync(clipboard);
            _clipboard.TextChanged += text => _ = _connection.SendAsync(new ClipboardMessage(text));
        }
        else if (!enabled && _clipboard is not null)
        {
            _clipboard.Dispose();
            _clipboard = null;
        }
    }

    // ---------------- 파일 ----------------

    /// <summary>파일을 Host의 공유 폴더\Received로 보냅니다.</summary>
    public async Task<FileResultMessage> UploadAsync(string path, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        uint id = (uint)Interlocked.Increment(ref _nextUploadId);
        var completion = new TaskCompletionSource<FileResultMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _uploads[id] = completion;
        try
        {
            await _connection.SendFileAsync(id, path, progress, cancellationToken);
            return await completion.Task.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken);
        }
        finally
        {
            _uploads.TryRemove(id, out _);
        }
    }

    public async Task<FileListMessage> GetFileListAsync(CancellationToken cancellationToken)
    {
        _fileList = new TaskCompletionSource<FileListMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _connection.SendAsync(new FileListRequestMessage());
        return await _fileList.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
    }

    /// <summary>Host 공유 폴더의 파일을 folder에 받습니다. 저장된 파일 이름을 결과로 돌려줍니다.</summary>
    public async Task<FileResultMessage> DownloadAsync(string name, string folder, CancellationToken cancellationToken)
    {
        if (_downloadReceiver is null || _downloadFolder != folder)
        {
            _downloadReceiver?.Dispose();
            _downloadReceiver = new FileReceiver(folder);
            _downloadFolder = folder;
        }

        var completion = new TaskCompletionSource<FileResultMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingDownload = completion;
        await _connection.SendAsync(new FileDownloadMessage(name));
        return await completion.Task.WaitAsync(TimeSpan.FromMinutes(10), cancellationToken);
    }

    private TaskCompletionSource<FileResultMessage>? _pendingDownload;

    // ---------------- 전원 ----------------

    public async Task<PowerResultMessage> PowerAsync(string action, CancellationToken cancellationToken)
    {
        _power = new TaskCompletionSource<PowerResultMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _connection.SendAsync(new PowerActionMessage(action));
        return await _power.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
    }

    // ---------------- 해상도 (STEP 12) ----------------

    public async Task<DisplayModesMessage> GetDisplayModesAsync(CancellationToken cancellationToken)
    {
        _displayModes = new TaskCompletionSource<DisplayModesMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _displayResult = new TaskCompletionSource<DisplayResultMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _connection.SendAsync(new DisplayModesRequestMessage());
        Task finished = await Task.WhenAny(_displayModes.Task, _displayResult.Task).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        if (finished == _displayResult.Task)
        {
            throw new InvalidOperationException(_displayResult.Task.Result.Error ?? "해상도 목록을 받을 수 없습니다.");
        }

        return await _displayModes.Task;
    }

    /// <summary>width/height가 0이면 원래 해상도로</summary>
    public async Task<DisplayResultMessage> SetResolutionAsync(int width, int height, CancellationToken cancellationToken)
    {
        _displayResult = new TaskCompletionSource<DisplayResultMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _connection.SendAsync(new SetResolutionMessage(width, height));
        return await _displayResult.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
    }

    // ---------------- 소리 ----------------

    public bool AudioEnabled => _audio is not null;

    public async Task SetAudioAsync(bool enabled)
    {
        if (enabled && _audio is null && _connection.HasFeature(HostFeatures.Audio))
        {
            _audio = new AudioPlayer();
            await _connection.SendAsync(new AudioControlMessage("start"));
        }
        else if (!enabled && _audio is not null)
        {
            await _connection.SendAsync(new AudioControlMessage("stop"));
            _audio.Dispose();
            _audio = null;
        }
    }

    // ---------------- 수신 처리 ----------------

    private void OnControl(ControlMessage message)
    {
        switch (message)
        {
            case ClipboardMessage clipboard:
                _clipboard?.SetRemoteText(clipboard.Text);
                RemoteClipboardReceived?.Invoke(clipboard.Text);
                break;

            case FileListMessage list:
                _fileList?.TrySetResult(list);
                break;

            case FileResultMessage result when _uploads.TryGetValue(result.TransferId, out var upload):
                upload.TrySetResult(result);
                break;

            case FileResultMessage failed:
                // 다운로드 요청이 거부된 경우 (파일 없음 등)
                _pendingDownload?.TrySetResult(failed);
                break;

            case FileBeginMessage begin when begin.Direction == "download" && _downloadReceiver is not null:
                if (_downloadReceiver.Begin(begin) is { } rejected)
                {
                    _pendingDownload?.TrySetResult(rejected);
                }

                break;

            case FileEndMessage end when _downloadReceiver is not null:
                FileResultMessage saved = _downloadReceiver.End(end);
                _ = _connection.SendAsync(saved);
                _pendingDownload?.TrySetResult(saved);
                break;

            case FileCancelMessage cancel:
                _downloadReceiver?.Abort(cancel.TransferId);
                _pendingDownload?.TrySetResult(new FileResultMessage(cancel.TransferId, false, cancel.Reason ?? "취소됨"));
                break;

            case PowerResultMessage power:
                _power?.TrySetResult(power);
                break;

            case DisplayModesMessage modes:
                _displayModes?.TrySetResult(modes);
                break;

            case DisplayResultMessage result:
                _displayResult?.TrySetResult(result);
                break;

            case AudioFormatMessage format:
                _audio?.Configure(format);
                break;
        }
    }

    private void OnBinary(BinaryMessage message)
    {
        if (message.Kind == MessageKind.FileChunk && _downloadReceiver?.Chunk(message.Data) is { } failure)
        {
            _pendingDownload?.TrySetResult(failure);
        }
        else if (message.Kind == MessageKind.Audio)
        {
            _audio?.Add(message.Data);
        }
    }

    public void Dispose()
    {
        _connection.ControlReceived -= OnControl;
        _connection.BinaryReceived -= OnBinary;
        _clipboard?.Dispose();
        _audio?.Dispose();
        _downloadReceiver?.Dispose();
    }
}

/// <summary>Host에서 받은 16bit PCM을 스피커로 재생합니다 (지연을 줄이려고 버퍼는 0.5초까지만)</summary>
internal sealed class AudioPlayer : IDisposable
{
    private WaveOutEvent? _output;
    private BufferedWaveProvider? _buffer;

    public void Configure(AudioFormatMessage format)
    {
        Dispose();
        _buffer = new BufferedWaveProvider(new WaveFormat(format.SampleRate, 16, format.Channels))
        {
            BufferDuration = TimeSpan.FromSeconds(0.5),
            DiscardOnBufferOverflow = true
        };
        _output = new WaveOutEvent { DesiredLatency = 100 };
        _output.Init(_buffer);
        _output.Play();
        Log.Info($"Audio playback: {format.SampleRate} Hz, {format.Channels} ch");
    }

    public void Add(ReadOnlyMemory<byte> message)
    {
        if (_buffer is null || message.Length <= 4)
        {
            return;
        }

        byte[] pcm = message[4..].ToArray(); // 앞 4바이트는 순번
        _buffer.AddSamples(pcm, 0, pcm.Length);
    }

    public void Dispose()
    {
        _output?.Dispose();
        _output = null;
        _buffer = null;
    }
}
