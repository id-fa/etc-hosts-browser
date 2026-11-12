namespace EtcHostsBrowser;

/// <summary>
/// レスポンシブ確認用のデバイス定義。
/// UserAgent があるものはモバイルとして模擬し、Width が 0 のものは表示領域をウィンドウいっぱいに使う。
/// </summary>
public sealed record DevicePreset(string Name, int Width, int Height, string? UserAgent)
{
    public bool IsMobile => UserAgent != null;
    public bool HasSize => Width > 0;

    public override string ToString() => HasSize ? $"{Name} ({Width}×{Height})" : Name;

    private const string IPhoneUa =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1";
    private const string IPadUa =
        "Mozilla/5.0 (iPad; CPU OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1";
    private const string AndroidUa =
        "Mozilla/5.0 (Linux; Android 10; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Mobile Safari/537.36";

    public static readonly DevicePreset[] All =
    [
        new("デスクトップ", 0, 0, null),
        new("iPhone SE", 375, 667, IPhoneUa),
        new("iPhone 15", 393, 852, IPhoneUa),
        new("iPhone 15 Pro Max", 430, 932, IPhoneUa),
        new("Pixel 8", 412, 915, AndroidUa),
        new("Galaxy S23", 360, 780, AndroidUa),
        new("iPad", 820, 1180, IPadUa),
    ];
}
