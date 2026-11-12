using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace EtcHostsBrowser;

public sealed class CertResult
{
    public required string Host { get; init; }
    public required IPAddress Address { get; init; }
    public required int Port { get; init; }
    public SslProtocols Protocol { get; set; }
    public string CipherSuite { get; set; } = "";
    public SslPolicyErrors Errors { get; set; }
    public List<string> ChainStatus { get; } = [];
    /// <summary>先頭がサーバ証明書、以降が中間・ルート。</summary>
    public List<X509Certificate2> Chain { get; } = [];

    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"ホスト名      : {Host}");
        sb.AppendLine($"接続先        : {Address}:{Port}");
        sb.AppendLine($"プロトコル    : {Protocol}");
        sb.AppendLine($"暗号スイート  : {CipherSuite}");
        sb.AppendLine($"検証結果      : {DescribeErrors()}");
        foreach (var s in ChainStatus) sb.AppendLine($"                - {s}");

        for (int i = 0; i < Chain.Count; i++)
        {
            var c = Chain[i];
            string role = i == 0 ? "サーバ証明書" : i == Chain.Count - 1 && c.Subject == c.Issuer ? "ルート証明書" : "中間証明書";
            sb.AppendLine();
            sb.AppendLine($"━━ [{i}] {role} ━━━━━━━━━━━━━━━━━━━━");
            sb.AppendLine($"サブジェクト  : {c.Subject}");
            sb.AppendLine($"発行者        : {c.Issuer}");
            sb.AppendLine($"有効期間      : {c.NotBefore:yyyy-MM-dd HH:mm:ss} ～ {c.NotAfter:yyyy-MM-dd HH:mm:ss}{DescribeValidity(c)}");

            var names = SubjectAltNames(c);
            if (names.Count > 0)
            {
                sb.AppendLine($"別名 (SAN)    : {names[0]}");
                foreach (var n in names.Skip(1)) sb.AppendLine($"                {n}");
            }

            sb.AppendLine($"シリアル番号  : {c.SerialNumber}");
            sb.AppendLine($"署名方式      : {c.SignatureAlgorithm.FriendlyName}");
            sb.AppendLine($"公開鍵        : {c.PublicKey.Oid.FriendlyName} {KeySize(c)}");
            sb.AppendLine($"SHA-256 拇印  : {c.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256)}");
        }
        return sb.ToString();
    }

    /// <summary>JSON 出力用（機械可読）。</summary>
    public object ToJson() => new
    {
        connectedTo = $"{Address}:{Port}",
        protocol = Protocol.ToString(),
        cipherSuite = CipherSuite,
        trusted = Errors == SslPolicyErrors.None,
        validationErrors = Errors == SslPolicyErrors.None ? [] : Errors.ToString().Split(", "),
        chainStatus = ChainStatus,
        chain = Chain.Select(c => new
        {
            subject = c.Subject,
            issuer = c.Issuer,
            notBefore = c.NotBefore.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            notAfter = c.NotAfter.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            daysRemaining = (int)Math.Floor((c.NotAfter - DateTime.Now).TotalDays),
            subjectAltNames = SubjectAltNames(c),
            serialNumber = c.SerialNumber,
            signatureAlgorithm = c.SignatureAlgorithm.FriendlyName,
            sha256 = c.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256),
        }),
    };

    private string DescribeErrors()
    {
        if (Errors == SslPolicyErrors.None) return "OK（信頼された証明書）";
        var parts = new List<string>();
        if (Errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch)) parts.Add("ホスト名が証明書と一致しない");
        if (Errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors)) parts.Add("証明書チェーンを信頼できない");
        if (Errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable)) parts.Add("証明書が提示されなかった");
        return "NG（" + string.Join(" / ", parts) + "）";
    }

    private static string DescribeValidity(X509Certificate2 c)
    {
        var now = DateTime.Now;
        if (now < c.NotBefore) return "  ※まだ有効期間前";
        if (now > c.NotAfter) return "  ※期限切れ";
        return $"  （残り {(int)(c.NotAfter - now).TotalDays} 日）";
    }

    private static List<string> SubjectAltNames(X509Certificate2 c)
    {
        var list = new List<string>();
        foreach (var ext in c.Extensions)
        {
            if (ext is X509SubjectAlternativeNameExtension san)
            {
                list.AddRange(san.EnumerateDnsNames());
                list.AddRange(san.EnumerateIPAddresses().Select(a => a.ToString()));
            }
        }
        return list;
    }

    private static string KeySize(X509Certificate2 c)
    {
        using var rsa = c.GetRSAPublicKey();
        if (rsa != null) return $"{rsa.KeySize} bit";
        using var ecdsa = c.GetECDsaPublicKey();
        if (ecdsa != null) return $"{ecdsa.KeySize} bit";
        return "";
    }
}

public static class CertInspector
{
    /// <summary>
    /// 指定した IP に直接 TLS 接続し（SNI はホスト名）、サーバ証明書とチェーンを取得する。
    /// 証明書が不正でも接続を切らずに内容を返す。
    /// </summary>
    public static async Task<CertResult> FetchAsync(string host, IPAddress address, int port)
    {
        var result = new CertResult { Host = host, Address = address, Port = port };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var tcp = new TcpClient(address.AddressFamily);
        await tcp.ConnectAsync(address, port, cts.Token);

        using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = host,
            RemoteCertificateValidationCallback = (_, cert, chain, errors) =>
            {
                result.Errors = errors;
                if (chain != null && chain.ChainElements.Count > 0)
                {
                    // chain はコールバック後に破棄されるので複製して持ち出す
                    foreach (var element in chain.ChainElements)
                        result.Chain.Add(new X509Certificate2(element.Certificate));
                    foreach (var status in chain.ChainStatus)
                        result.ChainStatus.Add($"{status.Status}: {status.StatusInformation.Trim()}");
                }
                else if (cert != null)
                {
                    result.Chain.Add(new X509Certificate2(cert));
                }
                return true;
            },
        };
        await ssl.AuthenticateAsClientAsync(options, cts.Token);

        result.Protocol = ssl.SslProtocol;
        result.CipherSuite = ssl.NegotiatedCipherSuite.ToString();
        return result;
    }
}

/// <summary>証明書情報の表示ダイアログ。</summary>
public sealed class CertDialog : Form
{
    public CertDialog(CertResult result)
    {
        Text = $"SSL サーバ証明書 - {result.Host}";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(LogicalToDeviceUnits(760), LogicalToDeviceUnits(520));
        Padding = new Padding(10);

        var text = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            Font = new Font("MS Gothic", 10f),
            BackColor = SystemColors.Window,
            Text = result.ToText(),
        };

        var close = new Button { Text = "閉じる", AutoSize = true, DialogResult = DialogResult.Cancel };
        var detail = new Button { Text = "Windows の証明書ビューアで開く...", AutoSize = true };
        detail.Click += (_, _) =>
        {
            if (result.Chain.Count > 0)
                X509Certificate2UI.DisplayCertificate(result.Chain[0], Handle);
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Padding = new Padding(0, 8, 0, 0),
        };
        buttons.Controls.Add(close);
        buttons.Controls.Add(detail);

        Controls.Add(text);
        Controls.Add(buttons);
        CancelButton = close;

        Shown += (_, _) => { text.Select(0, 0); close.Focus(); };
    }
}
