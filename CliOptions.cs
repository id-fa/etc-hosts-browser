using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace EtcHostsBrowser;

/// <summary>コマンドライン引数。AI やスクリプトから操作するための入口。</summary>
public sealed class CliOptions
{
    public string? Url { get; private set; }
    /// <summary>--hosts / --hosts-file で渡された hosts 設定（この起動の間だけ有効）。</summary>
    public string? HostsText { get; private set; }
    public bool NoProxy { get; private set; }
    public bool PreferHttp { get; private set; }
    public DevicePreset? Device { get; private set; }
    public int? DebugPort { get; private set; }
    public string? ScreenshotPath { get; private set; }
    public bool FullPage { get; private set; }
    public bool Json { get; private set; }
    public string? OutputPath { get; private set; }
    public string? AuthUser { get; private set; }
    public string? AuthPassword { get; private set; }
    public int WaitMs { get; private set; } = 1000;
    public int TimeoutMs { get; private set; } = 30000;
    public bool Help { get; private set; }

    /// <summary>ページを開いて結果を JSON で出力し、すぐ終了するモード。</summary>
    public bool IsBatch => Json || ScreenshotPath != null;

    /// <summary>
    /// 保存済みの設定と違う条件で動かすモード。hosts 設定などを読み書きせず、この起動の間だけ有効にする。
    /// </summary>
    public bool IsIsolated => IsBatch || HostsText != null || NoProxy || PreferHttp || DebugPort != null;

    /// <summary>この起動専用の使い捨てプロファイルの場所（Program が設定する）。</summary>
    public string ProfileDir { get; set; } = "";

    // パイプ越しでも文字化けしないよう、ヘルプは ASCII のみで書く
    public const string Usage = """
        EtcHostsBrowser - a verification browser with app-local hosts mapping (no admin rights needed)

        Usage:
          EtcHostsBrowser.exe [URL] [options]

        Options (interactive window):
          --hosts "IP HOST [HOST...]"   hosts entries for this run only. Separate entries with ';'.
                                        Repeatable. Wildcards like *.example.com are allowed.
          --hosts-file PATH             read hosts entries from a file (hosts file format)
          --device NAME                 device preset: desktop, "iPhone SE", "iPhone 15",
                                        "iPhone 15 Pro Max", "Pixel 8", "Galaxy S23", iPad
          --viewport WxH                viewport size in CSS pixels (e.g. 1280x800)
          --basic-auth USER:PASS        answer HTTP Basic authentication automatically
          --no-proxy                    connect directly, ignoring the system proxy
          --prefer-http                 open URLs without a scheme as http:// and never upgrade
                                        http:// to https:// (automatic HTTPS upgrades and HSTS
                                        learned from visited sites; the HSTS preload list still applies)
          --remote-debugging-port N     expose the Chrome DevTools Protocol on 127.0.0.1:N so that
                                        Playwright / Puppeteer / browser MCP servers can attach
                                        (e.g. connectOverCDP("http://127.0.0.1:N"))

        Options (one-shot mode: open URL, print a JSON report to stdout, exit):
          --json                        print the report (connected IP, TLS certificate, status...)
          --screenshot PATH             save a PNG screenshot (implies one-shot mode)
          --full-page                   capture the whole page instead of the visible area
          --output PATH                 write the JSON report to a file instead of stdout
          --wait MS                     extra wait after load completes (default 1000)
          --timeout MS                  give up waiting for the page load (default 30000)

        Exit codes: 0 = page loaded, 1 = load failed or timed out, 2 = bad arguments

        Notes:
          Invalid TLS certificates never block the page; they are reported in the JSON instead.
          Cookies, cache and site storage live only while the window is open; every launch starts
          clean and its profile is deleted on exit.
          Runs with any of --hosts, --hosts-file, --no-proxy, --prefer-http, --remote-debugging-port
          or one-shot options do not read or modify the saved hosts settings.

        Example:
          EtcHostsBrowser.exe https://www.example.com/ --hosts "192.0.2.10 www.example.com" ^
              --device "iPhone 15" --screenshot shot.png --full-page --json
        """;

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        var hosts = new StringBuilder();
        (int W, int H)? viewport = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--"))
            {
                if (o.Url != null) throw new ArgumentException($"Unexpected argument: {arg}");
                o.Url = arg;
                continue;
            }

            string name = arg;
            string? inline = null;
            int eq = arg.IndexOf('=');
            if (eq > 0) { name = arg[..eq]; inline = arg[(eq + 1)..]; }

            string Value()
            {
                if (inline != null) return inline;
                if (i + 1 >= args.Length) throw new ArgumentException($"{name} requires a value");
                return args[++i];
            }
            int IntValue(int min, int max)
            {
                string v = Value();
                if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < min || n > max)
                    throw new ArgumentException($"{name}: '{v}' must be a number between {min} and {max}");
                return n;
            }

            switch (name)
            {
                case "--help" or "-h" or "/?": o.Help = true; break;
                case "--hosts": hosts.AppendLine(Value().Replace(';', '\n')); break;
                case "--hosts-file":
                    string path = Value();
                    try { hosts.AppendLine(File.ReadAllText(path)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        throw new ArgumentException($"--hosts-file: cannot read '{path}': {ex.Message}");
                    }
                    break;
                case "--device": o.Device = FindDevice(Value()); break;
                case "--viewport":
                    string size = Value();
                    var parts = size.ToLowerInvariant().Split('x');
                    if (parts.Length != 2
                        || !int.TryParse(parts[0], out int w) || !int.TryParse(parts[1], out int h)
                        || w < 100 || h < 100 || w > 8000 || h > 8000)
                        throw new ArgumentException($"--viewport: '{size}' must look like 1280x800");
                    viewport = (w, h);
                    break;
                case "--basic-auth":
                    string cred = Value();
                    int colon = cred.IndexOf(':');
                    if (colon < 0) throw new ArgumentException("--basic-auth must be USER:PASS");
                    o.AuthUser = cred[..colon];
                    o.AuthPassword = cred[(colon + 1)..];
                    break;
                case "--no-proxy": o.NoProxy = true; break;
                case "--prefer-http": o.PreferHttp = true; break;
                case "--remote-debugging-port": o.DebugPort = IntValue(1, 65535); break;
                case "--json": o.Json = true; break;
                case "--screenshot": o.ScreenshotPath = Path.GetFullPath(Value()); break;
                case "--full-page": o.FullPage = true; break;
                case "--output": o.OutputPath = Path.GetFullPath(Value()); break;
                case "--wait": o.WaitMs = IntValue(0, 600000); break;
                case "--timeout": o.TimeoutMs = IntValue(1000, 600000); break;
                default: throw new ArgumentException($"Unknown option: {name}");
            }
        }

        if (hosts.Length > 0)
        {
            var errors = new List<string>();
            HostsFile.Parse(hosts.ToString(), errors);
            if (errors.Count > 0)
                throw new ArgumentException("Invalid hosts entries (expected \"IP HOST [HOST...]\"): " +
                                            hosts.ToString().Trim().ReplaceLineEndings("; "));
            o.HostsText = hosts.ToString();
        }

        if (viewport is { } v)
        {
            o.Device = o.Device != null
                ? o.Device with { Width = v.W, Height = v.H }
                : new DevicePreset("custom", v.W, v.H, null);
        }

        if (o.IsBatch && string.IsNullOrWhiteSpace(o.Url))
            throw new ArgumentException("--json / --screenshot require a URL");

        return o;
    }

    private static DevicePreset FindDevice(string name)
    {
        if (name.Equals("desktop", StringComparison.OrdinalIgnoreCase)) return DevicePreset.All[0];
        return DevicePreset.All.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
               ?? throw new ArgumentException(
                   $"--device: unknown device '{name}'. Available: desktop, " +
                   string.Join(", ", DevicePreset.All.Skip(1).Select(d => d.Name)));
    }
}

/// <summary>GUI アプリから呼び出し元のコンソールやパイプへ文字を出すための補助。</summary>
public static class ConsoleIo
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    private static bool _attached;

    private static void Attach()
    {
        if (_attached) return;
        _attached = true;
        // 出力先がリダイレクトされていなければ、起動元のコンソールに書けるようにする
        AttachConsole(-1);
    }

    public static void Out(string text) => Write(text, error: false);
    public static void Error(string text) => Write(text, error: true);

    private static void Write(string text, bool error)
    {
        Attach();
        try
        {
            var writer = error ? Console.Error : Console.Out;
            writer.WriteLine(text);
            writer.Flush();
        }
        catch (IOException)
        {
            // 出力先がない（エクスプローラーからの起動など）
        }
    }
}
