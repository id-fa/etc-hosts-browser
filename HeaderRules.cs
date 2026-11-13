using System.Text.RegularExpressions;

namespace EtcHostsBrowser;

/// <summary>応答ヘッダーの上書きルール 1 件。<see cref="Value"/> が null のときは、そのヘッダーを削除する。</summary>
public sealed partial record HeaderRule(string Pattern, string Name, string? Value)
{
    private readonly Regex _regex = new(
        "^" + Regex.Escape(Pattern).Replace("\\*", ".*") + "$",
        RegexOptions.CultureInvariant | RegexOptions.Singleline);

    public bool IsMatch(string url) => _regex.IsMatch(url);

    /// <summary>CDP の Fetch.enable に渡す urlPattern。こちらは ? と \ も特別扱いするので打ち消しておく。</summary>
    public string FetchPattern => Pattern.Replace("\\", "\\\\").Replace("?", "\\?");

    public override string ToString() => Value == null ? $"{Pattern} -{Name}" : $"{Pattern} {Name}: {Value}";
}

/// <summary>
/// 応答ヘッダーの上書き設定の解析。1 行に「URLパターン  ヘッダー名: 値」（削除は「URLパターン  -ヘッダー名」）。
/// </summary>
public static partial class HeaderRules
{
    // HTTP のヘッダー名に使える文字（RFC 9110 の token）
    [GeneratedRegex(@"^[A-Za-z0-9!#$%&'*+.^_`|~-]+$")]
    private static partial Regex NameRegex();

    public static List<HeaderRule> Parse(string text, List<string>? errors = null)
    {
        var rules = new List<HeaderRule>();
        int lineNo = 0;
        foreach (string raw in text.Split('\n'))
        {
            lineNo++;
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            int space = line.IndexOfAny([' ', '\t']);
            string pattern = space < 0 ? line : line[..space];
            string rest = space < 0 ? "" : line[space..].Trim();

            string name;
            string? value;
            if (rest.StartsWith('-'))
            {
                name = rest[1..].Trim();
                value = null;
            }
            else
            {
                int colon = rest.IndexOf(':');
                if (colon < 0)
                {
                    errors?.Add($"{lineNo} 行目: 「URLパターン  ヘッダー名: 値」の形で書いてください: {line}");
                    continue;
                }
                name = rest[..colon].Trim();
                value = rest[(colon + 1)..].Trim();
            }

            if (pattern != "*" && !pattern.StartsWith('*') && !pattern.Contains("://"))
            {
                errors?.Add($"{lineNo} 行目: URL パターンは「*」か「https://...」のように書いてください: {pattern}");
                continue;
            }
            if (!NameRegex().IsMatch(name))
            {
                errors?.Add($"{lineNo} 行目: ヘッダー名が正しくありません: {name}");
                continue;
            }
            if (value != null && value.Any(c => c < 0x20 || c == 0x7f))
            {
                errors?.Add($"{lineNo} 行目: 値に使えない文字が含まれています");
                continue;
            }

            rules.Add(new HeaderRule(pattern, name, value));
        }
        return rules;
    }

    /// <summary>元の応答ヘッダーに、URL が一致するルールを適用した結果を返す。一致するルールがなければ null。</summary>
    public static List<KeyValuePair<string, string>>? Apply(
        IReadOnlyList<HeaderRule> rules, string url, IEnumerable<KeyValuePair<string, string>> original)
    {
        var matched = rules.Where(r => r.IsMatch(url)).ToList();
        if (matched.Count == 0) return null;

        // ルールが触れる名前のヘッダーは元の応答から取り除き、指定された値だけを入れ直す
        var result = original
            .Where(h => !matched.Any(r => r.Name.Equals(h.Key, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        result.AddRange(matched.Where(r => r.Value != null)
                               .Select(r => new KeyValuePair<string, string>(r.Name, r.Value!)));
        return result;
    }
}
