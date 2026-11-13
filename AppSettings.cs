using System.Text.Json;

namespace EtcHostsBrowser;

/// <summary>保存先はすべてユーザー領域（%LOCALAPPDATA%\EtcHostsBrowser）。管理者権限は不要。</summary>
public sealed class AppSettings
{
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EtcHostsBrowser");

    public static string HostsPath => Path.Combine(DataDir, "hosts.txt");
    public static string HeadersPath => Path.Combine(DataDir, "headers.txt");
    /// <summary>起動ごとの使い捨てプロファイル（Cookie・キャッシュなど）の置き場。終了時に消す。</summary>
    public static string SessionsDir => Path.Combine(DataDir, "Sessions");
    /// <summary>以前の版が使っていた、Cookie が残り続けるプロファイル。見つけたら削除する。</summary>
    public static string LegacyWebViewDataDir => Path.Combine(DataDir, "WebView2");
    private static string SettingsPath => Path.Combine(DataDir, "settings.json");

    /// <summary>システムのプロキシを使わず直接接続する。</summary>
    public bool NoProxy { get; set; }

    /// <summary>
    /// プロトコルを省略した URL を http で開き、https への自動昇格（HTTPS アップグレード・HSTS）もさせない。
    /// </summary>
    public bool PreferHttp { get; set; }

    /// <summary>CORS を含む同一オリジンポリシーを無効にする。</summary>
    public bool DisableCors { get; set; }

    /// <summary>選択中のデバイス名。</summary>
    public string Device { get; set; } = "";

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 壊れていたら既定値で起動する
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string LoadHostsText()
    {
        try
        {
            return File.Exists(HostsPath) ? File.ReadAllText(HostsPath) : DefaultHostsText;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DefaultHostsText;
        }
    }

    public static void SaveHostsText(string text)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(HostsPath, text);
    }

    public static string LoadHeaderText()
    {
        try
        {
            return File.Exists(HeadersPath) ? File.ReadAllText(HeadersPath) : DefaultHeaderText;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DefaultHeaderText;
        }
    }

    public static void SaveHeaderText(string text)
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(HeadersPath, text);
    }

    private const string DefaultHeaderText =
        "# URLパターン  ヘッダー名: 値        （応答ヘッダーを追加・上書き）\r\n" +
        "# URLパターン  -ヘッダー名           （応答ヘッダーを削除）\r\n" +
        "#\r\n" +
        "# https://api.example.com/*  Access-Control-Allow-Origin: https://www.example.com\r\n" +
        "# https://api.example.com/*  Access-Control-Allow-Credentials: true\r\n" +
        "# https://www.example.com/*  -Content-Security-Policy\r\n";

    private const string DefaultHostsText =
        "# hosts ファイルと同じ書式で書きます（このアプリの中だけで有効）\r\n" +
        "# IPアドレス  ホスト名 [ホスト名 ...]\r\n" +
        "#\r\n" +
        "# 192.0.2.10  www.example.com example.com\r\n" +
        "# 192.0.2.10  *.example.com\r\n";
}
