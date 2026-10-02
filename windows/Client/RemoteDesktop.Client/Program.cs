using RemoteDesktop.Core;
using RemoteDesktop.Host.Engine;

namespace RemoteDesktop.Client;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 같은 사용자에게 앱은 하나만 (두 번째 실행은 조용히 종료)
        using var single = new Mutex(true, @"Local\RemoteDesktop.App", out bool first);
        if (!first)
        {
            MessageBox.Show("Remote Desktop이 이미 실행 중입니다. 알림 영역(트레이) 아이콘을 확인하세요.", "Remote Desktop");
            return;
        }

        Log.SetFile(AppPaths.GetFile("app.log"));
        NativeMethods.EnablePerMonitorDpiAwareness();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (_, e) => Log.Error($"UI error: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error($"Unhandled: {e.ExceptionObject}");

        Application.Run(new MainForm(startInBackground: args.Contains("--background")));
    }
}
