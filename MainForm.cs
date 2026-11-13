using System.Net;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace EtcHostsBrowser;

public sealed class MainForm : Form
{
    private const string AppTitle = "etc-hosts-browser";

    /// <summary>オリジン（scheme://host:port）ごとの接続情報。CDP の Network.responseReceived から集める。</summary>
    private sealed class ConnInfo
    {
        public string? Ip;
        public int Port;
        public string? Protocol;
        public string? Issuer;
        public DateTimeOffset? ValidTo;
    }

    private sealed record NavResult(bool Success, CoreWebView2WebErrorStatus Error, int HttpStatus);

    private readonly CliOptions _cli;
    private readonly AppSettings _settings = AppSettings.Load();
    /// <summary>false のときは一時的な起動（コマンドラインで条件を指定）なので、設定を保存しない。</summary>
    private readonly bool _persist;
    private string _hostsText;
    private List<HostRule> _rules;
    private readonly Dictionary<string, ConnInfo> _conn = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CoreWebView2WebErrorStatus> _certErrors = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _startUrl;

    private sealed record SavedCookie(string Name, string Value, string Domain, string Path, DateTime Expires,
                                      bool IsHttpOnly, bool IsSecure, CoreWebView2CookieSameSiteKind SameSite, bool IsSession);
    private List<SavedCookie>? _carriedCookies;

    // ワンショットモード用
    private readonly TaskCompletionSource<NavResult> _navDone = new();
    private bool _batchStarted;
    private bool _cliAuthUsed;
    private bool _authGaveUp;

    private WebView2? _webView;
    private string? _defaultUserAgent;
    private DevicePreset _device = DevicePreset.All[0];
    private bool _restarting;
    private bool _navigationFailed;
    private bool _hstsClearPending;

    private readonly Panel _hostPanel;
    private readonly Button _back, _forward, _reload, _shot, _cert, _hosts;
    private readonly TextBox _address;
    private readonly ComboBox _deviceBox;
    private readonly ToolStripStatusLabel _ipLabel, _tlsLabel, _msgLabel, _hostsLabel;

    private CoreWebView2? Core => _webView?.CoreWebView2;

    // ワンショットモードでは作業中のウィンドウからフォーカスを奪わない
    protected override bool ShowWithoutActivation => _cli.IsBatch;

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        // ワンショットモードでは、画面より大きいデバイス（iPad など）も欠けずに表示できるよう
        // ウィンドウサイズの上限を外す（WM_GETMINMAXINFO の ptMaxTrackSize）
        const int WM_GETMINMAXINFO = 0x0024;
        if (m.Msg == WM_GETMINMAXINFO && _cli.IsBatch)
        {
            const int maxTrackSizeOffset = 24; // POINT 3 個分
            System.Runtime.InteropServices.Marshal.WriteInt32(m.LParam, maxTrackSizeOffset, 20000);
            System.Runtime.InteropServices.Marshal.WriteInt32(m.LParam, maxTrackSizeOffset + 4, 20000);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel) return;

        // プロファイルは終了時にフォルダごと消すが、ファイルが掴まれて消し損ねた場合に備えて
        // Cookie だけは先に確実に消しておく
        try
        {
            Core?.CookieManager.DeleteAllCookies();
        }
        catch (Exception)
        {
            // 終了処理なので失敗しても続ける
        }
    }

    /// <summary>ワンショットモードで、指定デバイスの表示領域がウィンドウに収まるよう広げる。</summary>
    private void FitWindowToDevice()
    {
        if (!_cli.IsBatch || !_device.HasSize) return;
        float scale = DeviceDpi / 96f;
        int needW = (int)Math.Ceiling(_device.Width * scale);
        int needH = (int)Math.Ceiling(_device.Height * scale);
        int chromeH = ClientSize.Height - _hostPanel.ClientSize.Height;
        ClientSize = new Size(Math.Max(ClientSize.Width, needW), Math.Max(ClientSize.Height, needH + chromeH));
    }

    public MainForm(CliOptions cli)
    {
        _cli = cli;
        _startUrl = cli.Url;
        _persist = !cli.IsIsolated;
        _hostsText = cli.HostsText ?? AppSettings.LoadHostsText();
        _rules = HostsFile.Parse(_hostsText);
        if (cli.NoProxy) _settings.NoProxy = true;
        if (cli.PreferHttp) _settings.PreferHttp = true;

        Text = AppTitle;
        ClientSize = new Size(LogicalToDeviceUnits(1200), LogicalToDeviceUnits(800));
        StartPosition = FormStartPosition.CenterScreen;
        if (cli.IsBatch)
        {
            // 描画は必要なので非表示にはせず、画面外に置く
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-30000, -30000);
            ShowInTaskbar = false;
        }

        // ── ツールバー ──
        _back = MakeButton("←", "戻る");
        _forward = MakeButton("→", "進む");
        _reload = MakeButton("更新", "再読み込み (F5)");
        _address = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(4, 5, 4, 3) };
        _deviceBox = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = LogicalToDeviceUnits(190),
            Margin = new Padding(2, 5, 2, 3),
        };
        _shot = MakeButton("スクリーンショット ▾", "スクリーンショットを PNG で保存");
        _cert = MakeButton("証明書", "SSL サーバ証明書を表示");
        _hosts = MakeButton("hosts 設定", "アプリ内 hosts を編集");

        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 8,
            RowCount = 1,
            Padding = new Padding(4, 2, 4, 2),
        };
        for (int i = 0; i < 8; i++)
            bar.ColumnStyles.Add(i == 3 ? new ColumnStyle(SizeType.Percent, 100) : new ColumnStyle(SizeType.AutoSize));
        bar.Controls.AddRange([_back, _forward, _reload, _address, _deviceBox, _shot, _cert, _hosts]);

        // ── ステータスバー ──
        _ipLabel = new ToolStripStatusLabel("接続先 IP: -")
        {
            Font = new Font(Font, FontStyle.Bold),
            BorderSides = ToolStripStatusLabelBorderSides.Right,
        };
        _tlsLabel = new ToolStripStatusLabel("") { BorderSides = ToolStripStatusLabelBorderSides.Right };
        _msgLabel = new ToolStripStatusLabel("") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _hostsLabel = new ToolStripStatusLabel("") { BorderSides = ToolStripStatusLabelBorderSides.Left };
        var status = new StatusStrip { ShowItemToolTips = true };
        status.Items.AddRange([_ipLabel, _tlsLabel, _msgLabel, _hostsLabel]);

        // ── ブラウザ領域 ──
        _hostPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(60, 60, 60) };
        _hostPanel.Resize += (_, _) => LayoutWebView();

        Controls.Add(_hostPanel);
        Controls.Add(bar);
        Controls.Add(status);

        // ── イベント ──
        _back.Click += (_, _) => { if (Core?.CanGoBack == true) Core.GoBack(); };
        _forward.Click += (_, _) => { if (Core?.CanGoForward == true) Core.GoForward(); };
        _reload.Click += (_, _) => Core?.Reload();
        _address.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = e.SuppressKeyPress = true;
            NavigateTo(_address.Text);
        };
        _address.Enter += (_, _) => BeginInvoke(_address.SelectAll);

        var shotMenu = new ContextMenuStrip();
        shotMenu.Items.Add("表示されている範囲", null, async (_, _) => await SaveScreenshotAsync(fullPage: false));
        shotMenu.Items.Add("ページ全体", null, async (_, _) => await SaveScreenshotAsync(fullPage: true));
        _shot.Click += (_, _) => shotMenu.Show(_shot, new Point(0, _shot.Height));

        _cert.Click += async (_, _) => await ShowCertificateAsync();
        _hosts.Click += async (_, _) => await EditHostsAsync();

        _deviceBox.Items.AddRange(DevicePreset.All);
        _device = cli.Device
                  ?? (cli.IsBatch ? null : DevicePreset.All.FirstOrDefault(d => d.Name == _settings.Device))
                  ?? DevicePreset.All[0];
        if (!_deviceBox.Items.Contains(_device)) _deviceBox.Items.Add(_device);
        _deviceBox.SelectedItem = _device;
        _deviceBox.SelectedIndexChanged += async (_, _) =>
        {
            _device = (DevicePreset)_deviceBox.SelectedItem!;
            _settings.Device = _device.Name;
            if (_persist) _settings.Save();
            await ApplyDeviceAsync(reload: true);
        };

        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5) { Core?.Reload(); e.Handled = true; }
            else if (e.Control && e.KeyCode == Keys.L) { _address.Focus(); e.Handled = true; }
        };

        UpdateHostsLabel();
        Load += async (_, _) =>
        {
            FitWindowToDevice();
            await InitBrowserAsync(_startUrl);
        };
    }

    private Button MakeButton(string text, string tip)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(2, 3, 2, 3),
            Padding = new Padding(4, 1, 4, 1),
        };
        new ToolTip().SetToolTip(b, tip);
        return b;
    }

    // ───────────────────────── ブラウザの初期化・再起動 ─────────────────────────

    private string BuildBrowserArguments()
    {
        var args = new List<string>();
        if (_rules.Count > 0)
            args.Add($"--host-resolver-rules=\"{HostsFile.ToResolverRules(_rules)}\"");
        if (_settings.NoProxy)
            args.Add("--no-proxy-server");
        if (_settings.PreferHttp)
        {
            // http:// の URL を https:// に自動で昇格させる機能（失敗時だけ http に戻る）を止める
            args.Add("--disable-features=HttpsUpgrades,HttpsFirstBalancedModeAutoEnable,HttpsFirstBalancedMode");
        }
        if (_cli.DebugPort is { } port)
            args.Add($"--remote-debugging-port={port}");
        return string.Join(" ", args);
    }

    private async Task InitBrowserAsync(string? url)
    {
        _conn.Clear();
        _certErrors.Clear();
        SetMessage("ブラウザを起動しています...");

        var options = new CoreWebView2EnvironmentOptions(BuildBrowserArguments());

        // 直前のブラウザプロセスが終了しきっていないと、引数の違う環境は作れないので少し粘る
        for (int attempt = 1; ; attempt++)
        {
            var view = new WebView2 { Dock = DockStyle.Fill };
            _hostPanel.Controls.Add(view);
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, _cli.ProfileDir, options);
                await view.EnsureCoreWebView2Async(env);
                _webView = view;
                break;
            }
            catch (Exception ex)
            {
                _hostPanel.Controls.Remove(view);
                view.Dispose();
                if (attempt >= 6)
                {
                    SetMessage("ブラウザを起動できませんでした");
                    if (_cli.IsBatch)
                    {
                        FinishBatch(new() { ["success"] = false, ["error"] = "WebView2InitFailed: " + ex.Message }, 1);
                        return;
                    }
                    MessageBox.Show(this,
                        "WebView2 を初期化できませんでした。\r\n" +
                        "Microsoft Edge WebView2 Runtime がインストールされているか確認してください。\r\n\r\n" + ex.Message,
                        AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                await Task.Delay(700);
            }
        }

        var core = _webView.CoreWebView2;
        _defaultUserAgent ??= core.Settings.UserAgent;

        core.SourceChanged += (_, _) =>
        {
            if (!_address.Focused) _address.Text = DisplayUrl();
            UpdateStatus();
        };
        core.HistoryChanged += (_, _) => UpdateNavButtons();
        core.DocumentTitleChanged += (_, _) =>
            Text = string.IsNullOrEmpty(core.DocumentTitle) ? AppTitle : $"{core.DocumentTitle} - {AppTitle}";
        core.NavigationStarting += (_, _) =>
        {
            _navigationFailed = false;
            SetMessage("読み込み中...");
        };
        core.NavigationCompleted += (_, e) =>
        {
            _navigationFailed = !e.IsSuccess;

            // 自動応答できる Basic 認証の 401 は途中経過なので、その後の結果を待つ
            bool authPending = e.WebErrorStatus == CoreWebView2WebErrorStatus.ValidAuthenticationCredentialsRequired
                               && _cli.AuthUser != null && !_authGaveUp;
            if (!authPending)
                _navDone.TrySetResult(new NavResult(e.IsSuccess, e.WebErrorStatus, e.HttpStatusCode));
            _address.Text = DisplayUrl();
            SetMessage(e.IsSuccess
                       || e.WebErrorStatus is CoreWebView2WebErrorStatus.OperationCanceled
                           or CoreWebView2WebErrorStatus.ValidAuthenticationCredentialsRequired
                ? ""
                : $"読み込み失敗: {e.WebErrorStatus}");
            UpdateStatus();
        };

        // 別ウィンドウで開くリンクは同じ画面で開く
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            core.Navigate(e.Uri);
        };

        // Basic 認証は自前のダイアログで受ける（標準の入力欄はスマホ幅だと欠けるため）
        core.BasicAuthenticationRequested += (_, e) =>
        {
            // コマンドラインで渡された認証情報は 1 回だけ自動で返す（間違っていた場合の無限ループ防止）
            if (_cli.AuthUser != null && !_cliAuthUsed)
            {
                _cliAuthUsed = true;
                e.Response.UserName = _cli.AuthUser;
                e.Response.Password = _cli.AuthPassword ?? "";
                return;
            }
            if (_cli.IsBatch)
            {
                // ワンショットモードでは入力を待てないので打ち切る
                _authGaveUp = true;
                e.Cancel = true;
                _navDone.TrySetResult(new NavResult(false,
                    CoreWebView2WebErrorStatus.ValidAuthenticationCredentialsRequired, 401));
                return;
            }

            var deferral = e.GetDeferral();
            BeginInvoke(() =>
            {
                try
                {
                    using var dialog = new AuthDialog(e.Uri, e.Challenge);
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                    {
                        e.Response.UserName = dialog.UserName;
                        e.Response.Password = dialog.Password;
                    }
                    else
                    {
                        e.Cancel = true;
                    }
                }
                finally
                {
                    deferral.Complete();
                }
            });
        };

        // SSL サーバ証明書が不正でも、記録だけして表示を続ける
        core.ServerCertificateErrorDetected += (_, e) =>
        {
            if (Uri.TryCreate(e.RequestUri, UriKind.Absolute, out var uri))
                _certErrors[OriginOf(uri)] = e.ErrorStatus;
            e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
            UpdateStatus();
        };

        // 接続先 IP と TLS 情報は DevTools Protocol から取る
        core.GetDevToolsProtocolEventReceiver("Network.responseReceived").DevToolsProtocolEventReceived += OnResponseReceived;
        await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");

        // hosts 反映のための再起動だった場合は、直前の Cookie を戻す
        if (_carriedCookies != null)
        {
            foreach (var c in _carriedCookies)
            {
                try
                {
                    var cookie = core.CookieManager.CreateCookie(c.Name, c.Value, c.Domain, c.Path);
                    cookie.IsHttpOnly = c.IsHttpOnly;
                    cookie.IsSecure = c.IsSecure;
                    cookie.SameSite = c.SameSite;
                    if (!c.IsSession) cookie.Expires = c.Expires;
                    core.CookieManager.AddOrUpdateCookie(cookie);
                }
                catch (Exception)
                {
                    // 戻せない Cookie があっても他は続ける
                }
            }
            _carriedCookies = null;
        }

        await ApplyDeviceAsync(reload: false);
        UpdateNavButtons();
        SetMessage("");

        if (!string.IsNullOrWhiteSpace(url) && url != "about:blank")
            NavigateTo(url);
        else
            _address.Focus();

        if (_cli.IsBatch && !_batchStarted)
        {
            _batchStarted = true;
            _ = RunBatchAsync();
        }
    }

    /// <summary>hosts 設定を反映するため、ブラウザプロセスごと作り直す。</summary>
    private async Task RestartBrowserAsync()
    {
        if (_restarting) return;
        _restarting = true;
        try
        {
            string? url = _webView?.Source?.ToString();

            // ブラウザプロセスを作り直すとセッション Cookie（ログイン状態など）が消えるので、
            // 「開いている間は有効」を保つために控えておいて後で戻す
            try
            {
                if (_webView?.CoreWebView2 is { } current)
                {
                    _carriedCookies = (await current.CookieManager.GetCookiesAsync(""))
                        .Select(c => new SavedCookie(c.Name, c.Value, c.Domain, c.Path, c.Expires,
                                                     c.IsHttpOnly, c.IsSecure, c.SameSite, c.IsSession))
                        .ToList();
                }
            }
            catch (Exception)
            {
                _carriedCookies = null;
            }

            var old = _webView;
            _webView = null;
            UpdateNavButtons();
            SetMessage("hosts 設定を反映しています...");

            if (old != null)
            {
                var exited = new TaskCompletionSource();
                var env = old.CoreWebView2?.Environment;
                if (env != null) env.BrowserProcessExited += (_, _) => exited.TrySetResult();

                _hostPanel.Controls.Remove(old);
                old.Dispose();

                if (env != null) await Task.WhenAny(exited.Task, Task.Delay(5000));
            }

            await InitBrowserAsync(url);
        }
        finally
        {
            _restarting = false;
        }
    }

    // ───────────────────────── ナビゲーション ─────────────────────────

    private void NavigateTo(string input)
    {
        var core = Core;
        if (core == null) return;

        string url = input.Trim();
        if (url.Length == 0) return;
        if (!url.Contains("://") && !url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            url = (_settings.PreferHttp ? "http://" : "https://") + url;

        try
        {
            core.Navigate(url);
            _webView!.Focus();
        }
        catch (ArgumentException)
        {
            SetMessage("URL が正しくありません");
        }
    }

    private string DisplayUrl()
    {
        string url = _webView?.Source?.ToString() ?? "";
        return url == "about:blank" ? "" : url;
    }

    private void UpdateNavButtons()
    {
        var core = Core;
        _back.Enabled = core?.CanGoBack == true;
        _forward.Enabled = core?.CanGoForward == true;
        _reload.Enabled = _shot.Enabled = _cert.Enabled = core != null;
    }

    // ───────────────────────── 接続先 IP・TLS 状態 ─────────────────────────

    private static string OriginOf(Uri uri) => uri.GetLeftPart(UriPartial.Authority);

    private static bool IsWeb(Uri? uri) => uri != null && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private void OnResponseReceived(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            if (!doc.RootElement.TryGetProperty("response", out var res)) return;
            if (!res.TryGetProperty("url", out var urlEl)) return;
            if (!Uri.TryCreate(urlEl.GetString(), UriKind.Absolute, out var uri) || !IsWeb(uri)) return;

            string origin = OriginOf(uri);
            if (!_conn.TryGetValue(origin, out var info)) _conn[origin] = info = new ConnInfo();

            // キャッシュから返った応答には IP が入らないので、そのときは前回の値を残す
            if (res.TryGetProperty("remoteIPAddress", out var ipEl) && ipEl.GetString() is { Length: > 0 } ip)
            {
                info.Ip = ip.Trim('[', ']');
                info.Port = res.TryGetProperty("remotePort", out var portEl) && portEl.TryGetInt32(out int port) ? port : 0;
            }

            if (res.TryGetProperty("securityDetails", out var sec) && sec.ValueKind == JsonValueKind.Object)
            {
                info.Protocol = sec.TryGetProperty("protocol", out var p) ? p.GetString() : null;
                info.Issuer = sec.TryGetProperty("issuer", out var iss) ? iss.GetString() : null;
                if (sec.TryGetProperty("validTo", out var vt) && vt.TryGetDouble(out double unix))
                    info.ValidTo = DateTimeOffset.FromUnixTimeSeconds((long)unix);
            }

            if (_settings.PreferHttp && uri.Scheme == Uri.UriSchemeHttps
                && res.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object
                && headers.EnumerateObject().Any(h => h.Name.Equals("Strict-Transport-Security", StringComparison.OrdinalIgnoreCase)))
                _ = ForgetHstsAsync();

            if (IsWeb(_webView?.Source) && OriginOf(_webView!.Source) .Equals(origin, StringComparison.OrdinalIgnoreCase))
                UpdateStatus();
        }
        catch (JsonException)
        {
            // 想定外の形式のイベントは無視する
        }
    }

    /// <summary>
    /// サイトが Strict-Transport-Security ヘッダーで覚えさせた「次からは https で」を忘れさせる。
    /// 個別に消す手段がなく、キャッシュの消去に相乗りしている（Chromium はこのとき HSTS も消す）。
    /// Chromium に組み込みの HSTS プリロードリストは消せない。
    /// </summary>
    private async Task ForgetHstsAsync()
    {
        if (_hstsClearPending) return;
        _hstsClearPending = true;
        try
        {
            // 1 ページの読み込みで何度も届くので、少し待ってまとめて 1 回だけ消す
            await Task.Delay(300);
            _hstsClearPending = false;
            if (Core is { } core)
                await core.CallDevToolsProtocolMethodAsync("Network.clearBrowserCache", "{}");
        }
        catch (Exception)
        {
            // ブラウザの再起動や終了と重なった場合など。次の応答でやり直す
        }
        finally
        {
            _hstsClearPending = false;
        }
    }

    private void UpdateStatus()
    {
        var uri = _webView?.Source;
        if (!IsWeb(uri))
        {
            _ipLabel.Text = "接続先 IP: -";
            _ipLabel.ForeColor = SystemColors.ControlText;
            _tlsLabel.Text = "";
            return;
        }

        string origin = OriginOf(uri!);
        _conn.TryGetValue(origin, out var info);
        bool mapped = HostsFile.Lookup(_rules, uri!.Host) != null;

        string ipText = info?.Ip != null
            ? (info.Ip.Contains(':') ? $"[{info.Ip}]" : info.Ip) + (info.Port > 0 ? $":{info.Port}" : "")
            : _navigationFailed ? "未接続" : "取得中...";
        _ipLabel.Text = $"接続先 IP: {ipText}" + (mapped ? "  [hosts 適用]" : "");
        _ipLabel.ForeColor = mapped ? Color.FromArgb(0, 90, 180) : SystemColors.ControlText;
        _ipLabel.ToolTipText = $"{uri.Host} の接続先";

        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            _tlsLabel.Text = "HTTP（暗号化なし）";
            _tlsLabel.ForeColor = SystemColors.GrayText;
        }
        else if (_certErrors.TryGetValue(origin, out var error))
        {
            _tlsLabel.Text = $"⚠ 証明書エラーを無視して表示中: {DescribeCertError(error)}";
            _tlsLabel.ForeColor = Color.FromArgb(200, 0, 0);
        }
        else if (info?.Protocol != null)
        {
            string until = info.ValidTo is { } v ? $" / 期限 {v.LocalDateTime:yyyy-MM-dd}" : "";
            _tlsLabel.Text = $"{info.Protocol} / 証明書 OK{until}";
            _tlsLabel.ForeColor = Color.FromArgb(0, 120, 0);
            _tlsLabel.ToolTipText = info.Issuer != null ? $"発行者: {info.Issuer}" : "";
        }
        else
        {
            _tlsLabel.Text = "HTTPS";
            _tlsLabel.ForeColor = SystemColors.ControlText;
        }
    }

    private static string DescribeCertError(CoreWebView2WebErrorStatus status) => status switch
    {
        CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect => "ホスト名が証明書と一致しない",
        CoreWebView2WebErrorStatus.CertificateExpired => "有効期限切れ（または期間前）",
        CoreWebView2WebErrorStatus.CertificateRevoked => "失効している",
        CoreWebView2WebErrorStatus.CertificateIsInvalid => "信頼できない証明書（自己署名など）",
        CoreWebView2WebErrorStatus.ClientCertificateContainsErrors => "クライアント証明書に問題がある",
        _ => status.ToString(),
    };

    private void SetMessage(string text) => _msgLabel.Text = text;

    private void UpdateHostsLabel()
    {
        _hostsLabel.Text = $"hosts: {_rules.Count} 件"
                           + (_settings.NoProxy ? " / プロキシ不使用" : "")
                           + (_settings.PreferHttp ? " / http 優先" : "")
                           + (_cli.DebugPort is { } port ? $" / CDP 127.0.0.1:{port}" : "")
                           + (_persist ? "" : " / 一時セッション");
        _hostsLabel.ToolTipText = _rules.Count == 0
            ? "hosts 設定はありません"
            : string.Join("\r\n", _rules.Take(30).Select(r => $"{r.Address}  {r.Host}"));
    }

    // ───────────────────────── デバイス模擬（レスポンシブ確認） ─────────────────────────

    private void LayoutWebView()
    {
        var view = _webView;
        if (view == null) return;

        if (!_device.HasSize)
        {
            view.Dock = DockStyle.Fill;
            return;
        }

        // CSS ピクセル → 画面ピクセル
        float scale = DeviceDpi / 96f;
        int w = (int)Math.Floor(_device.Width * scale);
        int h = Math.Min((int)Math.Round(_device.Height * scale), _hostPanel.ClientSize.Height);
        view.Dock = DockStyle.None;
        view.SetBounds(Math.Max(0, (_hostPanel.ClientSize.Width - w) / 2), 0, w, h);
    }

    private async Task ApplyDeviceAsync(bool reload)
    {
        var core = Core;
        if (core == null) return;

        LayoutWebView();
        core.Settings.UserAgent = _device.UserAgent ?? _defaultUserAgent ?? core.Settings.UserAgent;

        try
        {
            if (_device.IsMobile)
            {
                // 表示サイズは WebView 自体の大きさで決め、ここでは「モバイルとして振る舞う」ことだけ指定する
                await core.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride",
                    """{"width":0,"height":0,"deviceScaleFactor":0,"mobile":true}""");
                await core.CallDevToolsProtocolMethodAsync("Emulation.setTouchEmulationEnabled",
                    """{"enabled":true,"maxTouchPoints":5}""");
            }
            else
            {
                await core.CallDevToolsProtocolMethodAsync("Emulation.clearDeviceMetricsOverride", "{}");
                await core.CallDevToolsProtocolMethodAsync("Emulation.setTouchEmulationEnabled", """{"enabled":false}""");
            }
        }
        catch (Exception ex)
        {
            SetMessage($"デバイス模擬の設定に失敗: {ex.Message}");
        }

        if (reload && IsWeb(_webView?.Source)) core.Reload();
    }

    // ───────────────────────── スクリーンショット ─────────────────────────

    private static async Task<byte[]> CaptureAsync(CoreWebView2 core, bool fullPage)
    {
        string parameters = """{"format":"png"}""";
        if (fullPage)
        {
            using var metrics = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.getLayoutMetrics", "{}"));
            var size = metrics.RootElement.GetProperty("cssContentSize");
            double width = size.GetProperty("width").GetDouble();
            // 長すぎるページは描画上限を超えて失敗するので打ち切る
            double height = Math.Min(size.GetProperty("height").GetDouble(), 16000);
            parameters = JsonSerializer.Serialize(new
            {
                format = "png",
                captureBeyondViewport = true,
                clip = new { x = 0, y = 0, width, height, scale = 1 },
            });
        }

        using var result = JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", parameters));
        return Convert.FromBase64String(result.RootElement.GetProperty("data").GetString()!);
    }

    private async Task SaveScreenshotAsync(bool fullPage)
    {
        var core = Core;
        if (core == null) return;

        try
        {
            byte[] png = await CaptureAsync(core, fullPage);

            string host = IsWeb(_webView?.Source) ? _webView!.Source.Host : "page";
            foreach (char c in Path.GetInvalidFileNameChars()) host = host.Replace(c, '_');

            using var dialog = new SaveFileDialog
            {
                Filter = "PNG 画像 (*.png)|*.png",
                FileName = $"{host}_{DateTime.Now:yyyyMMdd_HHmmss}.png",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            await File.WriteAllBytesAsync(dialog.FileName, png);
            SetMessage($"保存しました: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "スクリーンショットを保存できませんでした。\r\n\r\n" + ex.Message,
                AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ───────────────────────── 証明書表示 ─────────────────────────

    private async Task<CertResult> FetchCertificateAsync(Uri uri)
    {
        // ブラウザが実際に接続した IP → hosts 設定 → 通常の DNS の順で接続先を決める
        IPAddress? address = null;
        if (_conn.TryGetValue(OriginOf(uri), out var info) && info.Ip != null)
            IPAddress.TryParse(info.Ip, out address);
        address ??= HostsFile.Lookup(_rules, uri.Host);
        address ??= (await Dns.GetHostAddressesAsync(uri.Host)).First();

        return await CertInspector.FetchAsync(uri.Host, address, uri.Port);
    }

    private async Task ShowCertificateAsync()
    {
        var uri = _webView?.Source;
        if (uri == null || uri.Scheme != Uri.UriSchemeHttps)
        {
            MessageBox.Show(this, "HTTPS のページを表示しているときに使えます。", AppTitle,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _cert.Enabled = false;
        SetMessage("証明書を取得しています...");
        try
        {
            var result = await FetchCertificateAsync(uri);
            SetMessage("");
            using var dialog = new CertDialog(result);
            dialog.ShowDialog(this);
        }
        catch (Exception ex)
        {
            SetMessage("");
            MessageBox.Show(this,
                "証明書を取得できませんでした。\r\n（プロキシ経由の接続では直接取得できないことがあります）\r\n\r\n" + ex.Message,
                AppTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _cert.Enabled = Core != null;
        }
    }

    // ───────────────────────── ワンショットモード（--json / --screenshot） ─────────────────────────

    /// <summary>ページの読み込みを待ち、結果を JSON にまとめて出力して終了する。</summary>
    private async Task RunBatchAsync()
    {
        var report = new Dictionary<string, object?>();
        int exitCode = 1;
        try
        {
            var core = Core!;
            var nav = _navDone.Task;
            bool timedOut = await Task.WhenAny(nav, Task.Delay(_cli.TimeoutMs)) != nav;
            if (!timedOut && _cli.WaitMs > 0) await Task.Delay(_cli.WaitMs);

            var uri = _webView?.Source;
            ConnInfo? info = null;
            if (IsWeb(uri)) _conn.TryGetValue(OriginOf(uri!), out info);
            bool success = !timedOut && nav.Result.Success;

            report["success"] = success;
            report["error"] = timedOut ? "Timeout" : success ? null : nav.Result.Error.ToString();
            report["httpStatus"] = timedOut || nav.Result.HttpStatus == 0 ? null : nav.Result.HttpStatus;
            report["requestedUrl"] = _startUrl;
            report["url"] = uri?.ToString();
            report["title"] = core.DocumentTitle;
            report["remoteAddress"] = info?.Ip;
            report["remotePort"] = info is { Port: > 0 } ? info.Port : null;
            report["hostsApplied"] = IsWeb(uri) && HostsFile.Lookup(_rules, uri!.Host) != null;
            report["hostsRules"] = _rules.Select(r => $"{r.Address} {r.Host}").ToArray();
            report["device"] = _device.IsMobile || _device.HasSize ? _device.Name : "desktop";
            report["userAgent"] = core.Settings.UserAgent;

            try
            {
                using var size = JsonDocument.Parse(await core.ExecuteScriptAsync(
                    "({width: innerWidth, height: innerHeight, devicePixelRatio: devicePixelRatio})"));
                report["viewport"] = size.RootElement.Clone();
            }
            catch (Exception)
            {
                report["viewport"] = null;
            }

            if (uri != null && uri.Scheme == Uri.UriSchemeHttps)
            {
                var tls = new Dictionary<string, object?>
                {
                    ["protocol"] = info?.Protocol,
                    // ブラウザが検出した証明書エラー（無視して表示を続けている）
                    ["certificateError"] = _certErrors.TryGetValue(OriginOf(uri), out var certError) ? certError.ToString() : null,
                };
                try
                {
                    tls["certificate"] = (await FetchCertificateAsync(uri)).ToJson();
                }
                catch (Exception ex)
                {
                    // プロキシ経由などで直接取れないときは、ブラウザから得た概要だけ返す
                    tls["certificate"] = null;
                    tls["certificateFetchError"] = ex.Message;
                    tls["issuer"] = info?.Issuer;
                    tls["notAfter"] = info?.ValidTo?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
                }
                report["tls"] = tls;
            }
            else
            {
                report["tls"] = null;
            }

            if (_cli.ScreenshotPath != null)
            {
                try
                {
                    byte[] png = await CaptureAsync(core, _cli.FullPage);
                    Directory.CreateDirectory(Path.GetDirectoryName(_cli.ScreenshotPath)!);
                    await File.WriteAllBytesAsync(_cli.ScreenshotPath, png);
                    report["screenshot"] = _cli.ScreenshotPath;
                }
                catch (Exception ex)
                {
                    report["screenshot"] = null;
                    report["screenshotError"] = ex.Message;
                }
            }

            exitCode = success ? 0 : 1;
        }
        catch (Exception ex)
        {
            report["success"] = false;
            report["error"] = "InternalError: " + ex.Message;
        }

        FinishBatch(report, exitCode);
    }

    private void FinishBatch(Dictionary<string, object?> report, int exitCode)
    {
        // 既定のエンコーダーは非 ASCII 文字を \uXXXX にするので、パイプ越しでも文字化けしない
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        try
        {
            if (_cli.OutputPath != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cli.OutputPath)!);
                File.WriteAllText(_cli.OutputPath, json);
            }
            else
            {
                ConsoleIo.Out(json);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ConsoleIo.Error("error: cannot write report: " + ex.Message);
            exitCode = 1;
        }

        Program.ExitCode = exitCode;
        Close();
    }

    // ───────────────────────── hosts 設定 ─────────────────────────

    private async Task EditHostsAsync()
    {
        if (_restarting) return;

        using var dialog = new HostsDialog(_hostsText, _settings.NoProxy, _settings.PreferHttp);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _hostsText = dialog.HostsText;
            _settings.NoProxy = dialog.NoProxy;
            _settings.PreferHttp = dialog.PreferHttp;
            if (_persist)
            {
                AppSettings.SaveHostsText(_hostsText);
                _settings.Save();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "設定を保存できませんでした。\r\n\r\n" + ex.Message, AppTitle,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _rules = HostsFile.Parse(dialog.HostsText);
        UpdateHostsLabel();
        await RestartBrowserAsync();
    }
}
