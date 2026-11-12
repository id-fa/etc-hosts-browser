namespace EtcHostsBrowser;

/// <summary>アプリ内 hosts の編集ダイアログ。</summary>
public sealed class HostsDialog : Form
{
    private readonly TextBox _editor;
    private readonly CheckBox _noProxy;

    public string HostsText => _editor.Text;
    public bool NoProxy => _noProxy.Checked;

    public HostsDialog(string hostsText, bool noProxy)
    {
        Text = "hosts 設定（このアプリの中だけで有効）";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(LogicalToDeviceUnits(640), LogicalToDeviceUnits(440));
        Padding = new Padding(10);

        var help = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(0, 0, 0, 6),
            Text = "Windows の hosts ファイルと同じ書式で書きます。「*.example.com」のワイルドカードも使えます。\r\n" +
                   "OK を押すとブラウザ部分を再起動して反映します（表示中のページは再読み込みされます）。",
        };

        _editor = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            Font = new Font("Consolas", 10f),
            Text = hostsText,
        };

        _noProxy = new CheckBox
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            Padding = new Padding(0, 6, 0, 6),
            Checked = noProxy,
            Text = "システムのプロキシを使わず直接接続する（プロキシ経由だと hosts 設定が効かない場合に ON）",
        };

        var ok = new Button { Text = "OK", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "キャンセル", AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(_editor);
        Controls.Add(help);
        Controls.Add(_noProxy);
        Controls.Add(buttons);

        AcceptButton = null; // Enter は改行に使う
        CancelButton = cancel;

        Shown += (_, _) => { _editor.SelectionStart = _editor.TextLength; _editor.Focus(); };
        FormClosing += OnFormClosing;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (DialogResult != DialogResult.OK) return;

        var errors = new List<string>();
        HostsFile.Parse(_editor.Text, errors);
        if (errors.Count == 0) return;

        e.Cancel = true;
        MessageBox.Show(this,
            "書式に誤りがあります。\r\n\r\n" + string.Join("\r\n", errors.Take(15)),
            "hosts 設定", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
