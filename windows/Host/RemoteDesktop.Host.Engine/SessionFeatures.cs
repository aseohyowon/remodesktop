using RemoteDesktop.Core;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.Host.Engine;

/// <summary>
/// STEP 9 부가 기능: 클립보드, 파일 전송, 오디오, 전원 동작.
/// Host 옵션으로 각각 끌 수 있고, 화면만 보기(--view-only)면 입력성 기능은 모두 꺼집니다.
/// </summary>
internal sealed class SessionFeatures : IDisposable
{
    private readonly HostServer _server;
    private readonly MessageChannel _channel;
    private readonly string _remote;
    private readonly string _clientName;
    private readonly CancellationToken _cancellationToken;
    private readonly ClipboardSync? _clipboard;
    private readonly FileReceiver? _files;
    private readonly AudioStreamer? _audio;
    private int _nextTransferId;

    public SessionFeatures(HostServer server, MessageChannel channel, string remote, string clientName, CancellationToken cancellationToken)
    {
        _server = server;
        _channel = channel;
        _remote = remote;
        _clientName = clientName;
        _cancellationToken = cancellationToken;
        HostOptions options = server.Options;

        if (options.AllowClipboard && !options.ViewOnly)
        {
            _clipboard = new ClipboardSync(server.ClipboardFactory());
            _clipboard.TextChanged += text => _ = SendAsync(new ClipboardMessage(text));
        }

        if (options.AllowFileTransfer && !options.ViewOnly)
        {
            _files = new FileReceiver(Path.Combine(options.SharedFolder, "Received"));
        }

        if (options.AllowAudio)
        {
            _audio = new AudioStreamer(channel, cancellationToken);
        }
    }

    /// <summary>부가 기능 메시지면 처리하고 true</summary>
    public async Task<bool> TryHandleAsync(ReceivedMessage message)
    {
        if (message.Binary is { Kind: MessageKind.FileChunk } chunk)
        {
            if (_files?.Chunk(chunk.Data) is { } failure)
            {
                await SendAsync(failure);
            }

            return true;
        }

        switch (message.Control)
        {
            case ClipboardMessage clipboard when _clipboard is not null:
                _clipboard.SetRemoteText(clipboard.Text);
                return true;

            case FileListRequestMessage when _files is not null:
                await SendAsync(ListSharedFolder());
                return true;

            case FileDownloadMessage download when _files is not null:
                StartDownload(download.Name);
                return true;

            case FileBeginMessage begin when _files is not null && begin.Direction == "upload":
                AccessLog.Write("file_upload_begin", _remote, _clientName, detail: FileTransferProtocol.SanitizeFileName(begin.Name));
                if (_files.Begin(begin) is { } rejected)
                {
                    await SendAsync(rejected);
                }

                return true;

            case FileEndMessage end when _files is not null:
                FileResultMessage result = _files.End(end);
                AccessLog.Write(result.Success ? "file_upload" : "file_upload_failed", _remote, _clientName, detail: result.SavedAs ?? result.Error);
                await SendAsync(result);
                return true;

            case FileCancelMessage cancel when _files is not null:
                _files.Abort(cancel.TransferId);
                return true;

            case FileResultMessage downloadResult:
                Log.Info($"Download {downloadResult.TransferId} {(downloadResult.Success ? "completed" : $"failed: {downloadResult.Error}")}");
                return true;

            case AudioControlMessage audio when _audio is not null:
                if (audio.Action == "start")
                {
                    try
                    {
                        await _audio.StartAsync();
                    }
                    catch (Exception exception) when (exception is NotSupportedException or System.Runtime.InteropServices.COMException)
                    {
                        Log.Warn($"오디오를 시작할 수 없습니다: {exception.Message}");
                    }
                }
                else
                {
                    _audio.Stop();
                }

                return true;

            case PowerActionMessage power:
                await HandlePowerAsync(power.Action);
                return true;

            default:
                return false;
        }
    }

    private FileListMessage ListSharedFolder()
    {
        string folder = _server.Options.SharedFolder;
        Directory.CreateDirectory(folder);
        FileEntry[] files = new DirectoryInfo(folder)
            .EnumerateFiles()
            .Where(f => !f.Name.StartsWith('.') && (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(500)
            .Select(f => new FileEntry(f.Name, f.Length, f.LastWriteTimeUtc))
            .ToArray();
        return new FileListMessage(folder, files);
    }

    /// <summary>공유 폴더 바로 아래의 파일만 보낼 수 있습니다 (하위 폴더, 상위 경로 불가).</summary>
    private void StartDownload(string requestedName)
    {
        uint id = (uint)Interlocked.Increment(ref _nextTransferId);
        string? name = FileTransferProtocol.SanitizeFileName(requestedName);
        string folder = Path.GetFullPath(_server.Options.SharedFolder);
        string? path = name is null ? null : Path.GetFullPath(Path.Combine(folder, name));

        if (path is null || Path.GetDirectoryName(path) != folder.TrimEnd(Path.DirectorySeparatorChar) || !File.Exists(path))
        {
            _ = SendAsync(new FileResultMessage(id, false, "공유 폴더에 그 파일이 없습니다."));
            return;
        }

        AccessLog.Write("file_download", _remote, _clientName, detail: name);
        _ = Task.Run(async () =>
        {
            try
            {
                await FileTransferProtocol.SendFileAsync(_channel, id, path, "download", null, _cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                await SendAsync(new FileCancelMessage(id, exception.Message));
            }
        });
    }

    private async Task HandlePowerAsync(string action)
    {
        bool allowed = !_server.Options.ViewOnly && (action == "lock" || _server.Options.AllowPower);
        if (!allowed)
        {
            await SendAsync(new PowerResultMessage(action, false,
                _server.Options.ViewOnly ? "화면 보기 전용 연결입니다." : "Host에서 허용하지 않은 동작입니다. (Host를 --allow-power로 실행)"));
            return;
        }

        AccessLog.Write("power", _remote, _clientName, detail: action);
        Log.Warn($"Power action requested by {_clientName}: {action}");
        try
        {
            await SendAsync(new PowerResultMessage(action, true)); // 재부팅 전에 결과부터 보냅니다
            _server.PowerController.Execute(action);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            await SendAsync(new PowerResultMessage(action, false, exception.Message));
        }
    }

    private async Task SendAsync(ControlMessage message)
    {
        try
        {
            await _channel.SendControlAsync(message, _cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _clipboard?.Dispose();
        _files?.Dispose();
        _audio?.Dispose();
    }
}
