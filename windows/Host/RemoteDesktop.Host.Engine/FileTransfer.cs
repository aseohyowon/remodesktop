using System.Buffers.Binary;
using System.Security.Cryptography;
using RemoteDesktop.Core;
using RemoteDesktop.Protocol;

namespace RemoteDesktop.Host.Engine;

/// <summary>
/// 파일 전송 (Host와 Windows Client가 함께 사용)
/// file_begin → file_chunk(바이너리: [transfer_id u32][offset u64][data]) × n → file_end(sha256) → file_result
///
/// 안전 장치
/// - 파일 이름만 받습니다(경로 불가). ".." 등 위험한 이름, 예약된 이름(CON, NUL ...)은 거부
/// - 지정한 폴더 밖에는 쓰지 않으며, 같은 이름이 있으면 "이름 (1).확장자"로 저장
/// - 크기 제한, 조각 순서(offset) 확인, SHA-256 검증 후에만 최종 파일로 이름 변경
/// </summary>
public static class FileTransferProtocol
{
    public const int ChunkSize = 64 * 1024;
    public const int ChunkHeaderSize = 12;
    public const long MaxFileSize = 4L * 1024 * 1024 * 1024;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>받은 파일 이름을 안전한 이름으로. 쓸 수 없으면 null</summary>
    public static string? SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/') || name.Contains('\\') || name.Contains(':'))
        {
            return null;
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = new string(name.Where(c => !invalid.Contains(c) && !char.IsControl(c)).ToArray()).Trim().TrimEnd('.');
        if (cleaned.Length == 0 || cleaned.Length > 200 || cleaned is "." or "..")
        {
            return null;
        }

        return ReservedNames.Contains(Path.GetFileNameWithoutExtension(cleaned)) ? null : cleaned;
    }

    /// <summary>폴더 안에서 겹치지 않는 경로: report.pdf → report (1).pdf</summary>
    public static string UniquePath(string folder, string fileName)
    {
        string path = Path.Combine(folder, fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int i = 1; File.Exists(path); i++)
        {
            path = Path.Combine(folder, $"{stem} ({i}){extension}");
        }

        return path;
    }

    /// <summary>파일 하나 보내기 (begin → 조각들 → end)</summary>
    public static async Task SendFileAsync(
        MessageChannel channel,
        uint transferId,
        string path,
        string direction,
        IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        await channel.SendControlAsync(new FileBeginMessage(transferId, info.Name, info.Length, direction), cancellationToken);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] head = new byte[ChunkHeaderSize];
        byte[] buffer = new byte[ChunkSize];
        long offset = 0;

        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, useAsync: true))
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                BinaryPrimitives.WriteUInt32BigEndian(head, transferId);
                BinaryPrimitives.WriteInt64BigEndian(head.AsSpan(4), offset);
                hash.AppendData(buffer, 0, read);
                await channel.SendBinaryAsync(MessageKind.FileChunk, head, buffer.AsMemory(0, read), cancellationToken);
                offset += read;
                progress?.Report(offset);
            }
        }

        await channel.SendControlAsync(new FileEndMessage(transferId, Convert.ToHexString(hash.GetHashAndReset())), cancellationToken);
    }
}

/// <summary>여러 파일을 동시에 받을 수 있는 수신기. 연결이 끝나면 Dispose해서 덜 받은 파일을 지웁니다.</summary>
public sealed class FileReceiver : IDisposable
{
    private const int MaxConcurrent = 4;

    private readonly string _folder;
    private readonly long _maxSize;
    private readonly Dictionary<uint, Incoming> _transfers = new();

    public FileReceiver(string folder, long maxSize = FileTransferProtocol.MaxFileSize)
    {
        _folder = folder;
        _maxSize = maxSize;
    }

    /// <summary>받기 시작. 거부하면 실패 결과를 반환합니다(상대에게 보낼 메시지).</summary>
    public FileResultMessage? Begin(FileBeginMessage begin)
    {
        string? name = FileTransferProtocol.SanitizeFileName(begin.Name);
        if (name is null)
        {
            return new FileResultMessage(begin.TransferId, false, "허용되지 않는 파일 이름입니다.");
        }

        if (begin.Size < 0 || begin.Size > _maxSize)
        {
            return new FileResultMessage(begin.TransferId, false, $"파일이 너무 큽니다 (최대 {_maxSize / 1024 / 1024} MB).");
        }

        if (_transfers.Count >= MaxConcurrent || _transfers.ContainsKey(begin.TransferId))
        {
            return new FileResultMessage(begin.TransferId, false, "동시에 받을 수 있는 파일 수를 넘었습니다.");
        }

        Directory.CreateDirectory(_folder);
        string partial = Path.Combine(_folder, $".{Guid.NewGuid():N}.part");
        _transfers[begin.TransferId] = new Incoming(name, begin.Size, partial,
            new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None),
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256));
        return null;
    }

    /// <summary>조각 처리. 문제가 있으면 실패 결과(전송 취소)를 반환합니다.</summary>
    public FileResultMessage? Chunk(ReadOnlyMemory<byte> data)
    {
        if (data.Length < FileTransferProtocol.ChunkHeaderSize)
        {
            return null;
        }

        uint id = BinaryPrimitives.ReadUInt32BigEndian(data.Span);
        long offset = BinaryPrimitives.ReadInt64BigEndian(data.Span[4..]);
        ReadOnlyMemory<byte> body = data[FileTransferProtocol.ChunkHeaderSize..];

        if (!_transfers.TryGetValue(id, out Incoming? transfer))
        {
            return null; // 이미 취소된 전송
        }

        if (offset != transfer.Written || transfer.Written + body.Length > transfer.Size)
        {
            Abort(id);
            return new FileResultMessage(id, false, "전송 순서가 잘못되었거나 크기가 맞지 않습니다.");
        }

        transfer.Stream.Write(body.Span);
        transfer.Hash.AppendData(body.Span);
        transfer.Written += body.Length;
        return null;
    }

    /// <summary>전송 끝: 크기와 SHA-256을 확인한 뒤 최종 이름으로 저장합니다.</summary>
    public FileResultMessage End(FileEndMessage end)
    {
        if (!_transfers.Remove(end.TransferId, out Incoming? transfer))
        {
            return new FileResultMessage(end.TransferId, false, "알 수 없는 전송입니다.");
        }

        transfer.Stream.Dispose();
        string actual = Convert.ToHexString(transfer.Hash.GetHashAndReset());
        transfer.Hash.Dispose();

        if (transfer.Written != transfer.Size || !actual.Equals(end.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(transfer.PartialPath);
            return new FileResultMessage(end.TransferId, false, "파일이 손상되었습니다 (크기 또는 SHA-256 불일치).");
        }

        string final = FileTransferProtocol.UniquePath(_folder, transfer.Name);
        File.Move(transfer.PartialPath, final);
        Log.Info($"File received: {Path.GetFileName(final)} ({transfer.Size:N0} bytes)");
        return new FileResultMessage(end.TransferId, true, null, Path.GetFileName(final));
    }

    public void Abort(uint transferId)
    {
        if (_transfers.Remove(transferId, out Incoming? transfer))
        {
            transfer.Stream.Dispose();
            transfer.Hash.Dispose();
            TryDelete(transfer.PartialPath);
        }
    }

    public void Dispose()
    {
        foreach (uint id in _transfers.Keys.ToList())
        {
            Abort(id);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private sealed class Incoming(string name, long size, string partialPath, FileStream stream, IncrementalHash hash)
    {
        public string Name { get; } = name;
        public long Size { get; } = size;
        public string PartialPath { get; } = partialPath;
        public FileStream Stream { get; } = stream;
        public IncrementalHash Hash { get; } = hash;
        public long Written { get; set; }
    }
}
