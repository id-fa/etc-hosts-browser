namespace EtcHostsBrowser;

/// <summary>Basic 認証のユーザー名・パスワード入力ダイアログ。</summary>
public sealed class AuthDialog : Form
{
    private readonly TextBox _user = new() { Dock = DockStyle.Fill };
    private readonly TextBox _password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };

    public string UserName => _user.Text;
    public string Password => _password.Text;

    public AuthDialog(string uri, string challenge)
    {
        Text = "認証が必要です";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        string site = Uri.TryCreate(uri, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : uri;

        var ok = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "キャンセル", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 8, 0, 0),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        var table = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            MinimumSize = new Size(LogicalToDeviceUnits(380), 0),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var message = new Label
        {
            AutoSize = true,
            Margin = new Padding(3, 0, 3, 10),
            Text = $"{site} はユーザー名とパスワードを要求しています。" +
                   (string.IsNullOrEmpty(challenge) ? "" : $"\r\n{challenge}"),
        };
        table.Controls.Add(message, 0, 0);
        table.SetColumnSpan(message, 2);
        table.Controls.Add(new Label { Text = "ユーザー名", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        table.Controls.Add(_user, 1, 1);
        table.Controls.Add(new Label { Text = "パスワード", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        table.Controls.Add(_password, 1, 2);
        table.Controls.Add(buttons, 0, 3);
        table.SetColumnSpan(buttons, 2);

        Controls.Add(table);
        AcceptButton = ok;
        CancelButton = cancel;
    }
}
