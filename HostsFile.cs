using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace EtcHostsBrowser;

public sealed record HostRule(string Host, IPAddress Address);

/// <summary>hosts ファイル形式（"IPアドレス ホスト名 [ホスト名...]"）の解析と、Chromium 用ルールへの変換。</summary>
public static partial class HostsFile
{
    // 起動引数に埋め込むので、ホスト名に使える文字を厳しく制限する
    [GeneratedRegex(@"^(\*\.)?[a-z0-9_]([a-z0-9_-]*[a-z0-9_])?(\.[a-z0-9_]([a-z0-9_-]*[a-z0-9_])?)*$")]
    private static partial Regex HostPattern();

    public static List<HostRule> Parse(string text, List<string>? errors = null)
    {
        var rules = new List<HostRule>();
        var idn = new IdnMapping();
        var lines = text.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) continue;

            if (!IPAddress.TryParse(tokens[0], out var ip))
            {
                errors?.Add($"{i + 1} 行目: 「{tokens[0]}」は IP アドレスではありません");
                continue;
            }
            if (tokens.Length == 1)
            {
                errors?.Add($"{i + 1} 行目: ホスト名がありません");
                continue;
            }

            foreach (var token in tokens.Skip(1))
            {
                string host;
                try
                {
                    // 日本語ドメインなどは Punycode に変換する
                    host = token.StartsWith("*.")
                        ? "*." + idn.GetAscii(token[2..])
                        : idn.GetAscii(token);
                }
                catch (ArgumentException)
                {
                    errors?.Add($"{i + 1} 行目: 「{token}」はホスト名として不正です");
                    continue;
                }

                host = host.ToLowerInvariant();
                if (!HostPattern().IsMatch(host))
                {
                    errors?.Add($"{i + 1} 行目: 「{token}」はホスト名として不正です");
                    continue;
                }
                rules.Add(new HostRule(host, ip));
            }
        }
        return rules;
    }

    /// <summary>Chromium の --host-resolver-rules に渡す文字列を作る。</summary>
    public static string ToResolverRules(IEnumerable<HostRule> rules) =>
        string.Join(",", rules.Select(r =>
            r.Address.AddressFamily == AddressFamily.InterNetworkV6
                ? $"MAP {r.Host} [{r.Address}]"
                : $"MAP {r.Host} {r.Address}"));

    /// <summary>ホスト名に一致する最初のルールの IP を返す（なければ null）。</summary>
    public static IPAddress? Lookup(IEnumerable<HostRule> rules, string host)
    {
        host = host.ToLowerInvariant();
        foreach (var r in rules)
        {
            if (r.Host == host) return r.Address;
            if (r.Host.StartsWith("*.") && host.EndsWith(r.Host[1..])) return r.Address;
        }
        return null;
    }
}
