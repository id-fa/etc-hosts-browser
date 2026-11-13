namespace EtcHostsBrowser;

/// <summary>アプリ内 hosts と、応答ヘッダーの上書き・接続まわりの設定の編集ダイアログ。</summary>
public sealed class HostsDialog : Form
{
    private readonly TabControl _tabs;
    private readonly TextBox _editor;
    private readonly TextBox _headerEditor;
    private readonly CheckBox _noProxy;
    private readonly CheckBox _preferHttp;
    private readonly CheckBox _disableCors;

    public string HostsText => _editor.Text;
    public string HeaderText => _headerEditor.Text;
    public bool NoProxy => _noProxy.Checked;
    public bool PreferHttp => _preferHttp.Checked;
    public bool DisableCors => _disableCors.Checked;

    public HostsDialog(string hostsText, string headerText, bool noProxy, bool preferHttp, bool disableCors)
    {
        Text = "hosts 設定（このアプリの中だけで有効）";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(LogicalToDeviceUnits(680), LogicalToDeviceUnits(500));
        Padding = new Padding(10);

        _editor = MakeEditor(hostsText);
        var hostsPage = MakePage("hosts", _editor,
            "Windows の hosts ファイルと同じ書式で書きます。「*.example.com」のワイルドカードも使えます。\r\n" +
            "OK を押すとブラウザ部分を再起動して反映します（表示中のページは再読み込みされます）。");

        _headerEditor = MakeEditor(headerText);
        var headerPage = MakePage("応答ヘッダーの上書き", _headerEditor,
            "サーバからの応答ヘッダーを、ページに渡す前に書き換えます。1 行に「URLパターン  ヘッダー名: 値」。\r\n" +
            "URL パターンは「*」で任意の文字列に一致します。「URLパターン  -ヘッダー名」でそのヘッダーを削除します。");

        _tabs = new TabControl { Dock = DockStyle.Fill };
        _tabs.TabPages.Add(hostsPage);
        _tabs.TabPages.Add(headerPage);

        _noProxy = MakeCheckBox(noProxy, 6,
            "システムのプロキシを使わず直接接続する（プロキシ経由だと hosts 設定が効かない場合に ON）");
        _preferHttp = MakeCheckBox(preferHttp, 0,
            "プロトコルを省略した URL を http で開く（https への自動切り替えと HSTS を無効にする）");
        _disableCors = MakeCheckBox(disableCors, 0,
            "CORS（同一オリジンポリシー）を無効にする（ページの JS が他のサイトの内容を読めるようになります）");

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

        Controls.Add(_tabs);
        Controls.Add(_noProxy);
        Controls.Add(_preferHttp);
        Controls.Add(_disableCors);
        Controls.Add(buttons);

        AcceptButton = null; // Enter は改行に使う
        CancelButton = cancel;

        Shown += (_, _) => { _editor.SelectionStart = _editor.TextLength; _editor.Focus(); };
        FormClosing += OnFormClosing;
    }

    private static TextBox MakeEditor(string text) => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        AcceptsReturn = true,
        AcceptsTab = true,
        WordWrap = false,
        ScrollBars = ScrollBars.Both,
        Font = new Font("Consolas", 10f),
        Text = text,
    };

    private static TabPage MakePage(string title, TextBox editor, string helpText)
    {
        var page = new TabPage(title) { Padding = new Padding(6), UseVisualStyleBackColor = true };
        page.Controls.Add(editor);
        page.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(0, 0, 0, 6),
            Text = helpText,
        });
        return page;
    }

    private static CheckBox MakeCheckBox(bool isChecked, int topPadding, string text) => new()
    {
        Dock = DockStyle.Bottom,
        AutoSize = true,
        Padding = new Padding(0, topPadding, 0, 6),
        Checked = isChecked,
        Text = text,
    };

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (DialogResult != DialogResult.OK) return;

        var errors = new List<string>();
        HostsFile.Parse(_editor.Text, errors);
        int page = 0;
        if (errors.Count == 0)
        {
            HeaderRules.Parse(_headerEditor.Text, errors);
            page = 1;
        }
        if (errors.Count == 0) return;

        e.Cancel = true;
        _tabs.SelectedIndex = page;
        MessageBox.Show(this,
            "書式に誤りがあります。\r\n\r\n" + string.Join("\r\n", errors.Take(15)),
            "hosts 設定", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}
