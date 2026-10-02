using System.Text;
using RemoteDesktop.Host.Engine;

namespace RemoteDesktop.Host;

/// <summary>Host 관리 명령 (password, devices, totp, log)</summary>
internal static class HostCommands
{
    public static int Password(string[] args)
    {
        var settings = HostSettings.Load();
        switch (args)
        {
            case ["set"]:
                string first = ReadSecret($"새 비밀번호 ({HostSettings.MinPasswordLength}자 이상): ");
                string second = ReadSecret("한 번 더 입력: ");
                if (first != second)
                {
                    Console.Error.WriteLine("두 비밀번호가 다릅니다.");
                    return 1;
                }

                Console.WriteLine("비밀번호 키를 만드는 중... (PBKDF2)");
                settings.SetPassword(first);
                Console.WriteLine("비밀번호를 설정했습니다. (비밀번호 자체는 저장하지 않습니다)");
                return 0;

            case ["clear"]:
                settings.ClearPassword();
                Console.WriteLine("비밀번호를 삭제했습니다.");
                return 0;

            default:
                throw new ArgumentException("password set 또는 password clear");
        }
    }

    public static int Devices(string[] args)
    {
        var settings = HostSettings.Load();
        switch (args)
        {
            case []:
                var devices = settings.ListDevices();
                if (devices.Count == 0)
                {
                    Console.WriteLine("신뢰된 장치가 없습니다.");
                    return 0;
                }

                Console.WriteLine($"{"ID",-14} {"이름",-24} {"등록",-17} {"마지막 사용",-17} 상태");
                foreach (var device in devices)
                {
                    Console.WriteLine($"{device.Id,-14} {device.Name,-24} {device.Created.LocalDateTime:yyyy-MM-dd HH:mm}  {device.LastUsed.LocalDateTime:yyyy-MM-dd HH:mm}  {(device.Expired ? "만료" : "사용 가능")}");
                }

                return 0;

            case ["revoke", var id]:
                int removed = settings.RevokeDevice(id);
                Console.WriteLine(removed == 0 ? $"{id}를 찾을 수 없습니다." : $"{removed}개 장치의 등록을 해제했습니다.");
                return removed == 0 ? 1 : 0;

            default:
                throw new ArgumentException("devices 또는 devices revoke <ID|all>");
        }
    }

    public static int TotpCommand(string[] args)
    {
        var settings = HostSettings.Load();
        switch (args)
        {
            case ["enable"]:
                byte[] secret = Engine.Totp.GenerateSecret();
                Console.WriteLine("인증 앱(Google Authenticator, Microsoft Authenticator 등)에 아래 키를 등록하세요.");
                Console.WriteLine();
                Console.WriteLine($"  키   : {Engine.Totp.ToBase32(secret)}");
                Console.WriteLine($"  URI  : {Engine.Totp.ToOtpAuthUri(secret, settings.HostId)}");
                Console.WriteLine();
                string code = ReadLine("등록 후 앱에 표시된 6자리 코드를 입력하세요: ");
                if (!new Engine.Totp(secret).Verify(code.Trim(), DateTimeOffset.UtcNow))
                {
                    Console.Error.WriteLine("코드가 맞지 않아 2단계 인증을 켜지 않았습니다. 시계가 맞는지 확인하세요.");
                    return 1;
                }

                settings.SetTotpSecret(secret);
                Console.WriteLine("2단계 인증을 켰습니다. 접속 코드/비밀번호 로그인 시 6자리 코드가 필요합니다.");
                return 0;

            case ["disable"]:
                settings.ClearTotp();
                Console.WriteLine("2단계 인증을 껐습니다.");
                return 0;

            default:
                throw new ArgumentException("totp enable 또는 totp disable");
        }
    }

    public static int ShowLog(string[] args)
    {
        int count = args is [var n] && int.TryParse(n, out int parsed) ? parsed : 30;
        foreach (string line in AccessLog.ReadLast(count))
        {
            Console.WriteLine(line);
        }

        Console.WriteLine($"({AccessLog.FilePath})");
        return 0;
    }

    private static string ReadLine(string prompt)
    {
        Console.Write(prompt);
        return Console.ReadLine() ?? "";
    }

    /// <summary>입력한 글자를 화면에 표시하지 않습니다.</summary>
    private static string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
        {
            // PowerShell 파이프는 첫 줄 앞에 BOM(U+FEFF)을 붙일 수 있습니다.
            return (Console.ReadLine() ?? "").TrimStart('﻿');
        }

        var builder = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return builder.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                builder.Append(key.KeyChar);
            }
        }
    }
}
