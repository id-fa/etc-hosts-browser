using System.Diagnostics;

namespace EtcHostsBrowser;

internal static class Program
{
    /// <summary>ワンショットモードの終了コード（0 = 読み込み成功、1 = 失敗）。</summary>
    public static int ExitCode { get; set; }

    private static string SessionsDir => AppSettings.SessionsDir;

    [STAThread]
    private static int Main(string[] args)
    {
        CliOptions cli;
        try
        {
            cli = CliOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            ConsoleIo.Error($"error: {ex.Message}\n\nRun with --help for usage.");
            return 2;
        }

        if (cli.Help)
        {
            ConsoleIo.Out(CliOptions.Usage);
            return 0;
        }

        // Cookie やキャッシュは起動ごとの使い捨てプロファイルに置き、終了時に丸ごと消す。
        // プロファイルが起動ごとに別なので、複数のウィンドウを違う hosts 設定で同時に開いても衝突しない。
        CleanUpStaleSessions();
        cli.ProfileDir = Path.Combine(SessionsDir, Environment.ProcessId.ToString());

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(cli));

        TryDelete(cli.ProfileDir, attempts: 10);
        return ExitCode;
    }

    /// <summary>異常終了などで残った一時プロファイルを掃除する。</summary>
    private static void CleanUpStaleSessions()
    {
        TryDelete(AppSettings.LegacyWebViewDataDir, attempts: 1);
        if (!Directory.Exists(SessionsDir)) return;
        foreach (var dir in Directory.GetDirectories(SessionsDir))
        {
            if (int.TryParse(Path.GetFileName(dir), out int pid) && IsOurProcess(pid)) continue;
            TryDelete(dir, attempts: 1);
        }
    }

    private static bool IsOurProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            using var self = Process.GetCurrentProcess();
            return p.ProcessName == self.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static void TryDelete(string dir, int attempts)
    {
        // ブラウザプロセスが終了しきるまでファイルが掴まれているので、少し待ちながら試す
        for (int i = 0; i < attempts; i++)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (i + 1 < attempts) Thread.Sleep(300);
            }
        }
    }
}
