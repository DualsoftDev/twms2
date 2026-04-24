using System.Diagnostics;

namespace XgwxMaker;

internal sealed class MainForm : Form
{
    private readonly TextBox _txtIp       = new() { PlaceholderText = "10.20.30.40" };
    private readonly TextBox _txtViaIp    = new() { PlaceholderText = "(비우면 CPUUN)" };
    private readonly NumericUpDown _numSlot = new() { Minimum = 0, Maximum = 15, Value = 6 };
    private readonly NumericUpDown _numBase = new() { Minimum = 0, Maximum = 15, Value = 0 };
    private readonly TextBox _txtOut      = new() { PlaceholderText = "출력 .xgwx 경로" };
    private readonly Button  _btnOutPick  = new() { Text = "…", Width = 32 };
    private readonly Button  _btnRunSingle = new() { Text = "생성", Height = 36 };

    private readonly TextBox _txtCsv      = new() { PlaceholderText = "CSV 파일 (Name,Ip,ViaIp,Slot,Base)" };
    private readonly Button  _btnCsvPick  = new() { Text = "…", Width = 32 };
    private readonly Button  _btnCsvSample = new() { Text = "양식 CSV 저장", AutoSize = true };
    private readonly TextBox _txtOutDir   = new() { PlaceholderText = "출력 폴더" };
    private readonly Button  _btnOutDirPick = new() { Text = "…", Width = 32 };
    private readonly Button  _btnRunBatch = new() { Text = "일괄 생성", Height = 36 };

    private readonly TextBox _log = new()
    {
        Multiline = true,
        ReadOnly  = true,
        ScrollBars = ScrollBars.Vertical,
        Font = new Font("Consolas", 9f),
        BackColor = Color.FromArgb(30, 30, 30),
        ForeColor = Color.Gainsboro,
    };

    public MainForm()
    {
        Text = "XGWX Maker — XG5000 프로젝트 배치 생성";
        MinimumSize = new Size(720, 560);
        StartPosition = FormStartPosition.CenterScreen;

        Controls.Add(BuildLayout());

        _btnOutPick.Click   += (_, _) => PickSaveFile(_txtOut, "XG5000 Project (*.xgwx)|*.xgwx");
        _btnCsvPick.Click   += (_, _) => PickOpenFile(_txtCsv, "CSV (*.csv)|*.csv");
        _btnOutDirPick.Click += (_, _) => PickFolder(_txtOutDir);
        _btnRunSingle.Click += (_, _) => SafeRun(RunSingle);
        _btnRunBatch.Click  += (_, _) => SafeRun(RunBatch);
        _btnCsvSample.Click += (_, _) => SafeRun(ExportSampleCsv);

        // viaIP 입력 여부에 따라 slot/base 활성 토글
        _txtViaIp.TextChanged += (_, _) =>
        {
            bool hasVia = !string.IsNullOrWhiteSpace(_txtViaIp.Text);
            _numSlot.Enabled = hasVia;
            _numBase.Enabled = hasVia;
        };
        _numSlot.Enabled = false;
        _numBase.Enabled = false;

        Log("준비됨. (템플릿은 EXE 에 임베디드됨)");
    }

    private Control BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 4,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(BuildSingleGroup(), 0, 0);
        root.Controls.Add(BuildBatchGroup(),  0, 1);

        var lblLog = new Label { Text = "로그", AutoSize = true, Margin = new Padding(0, 8, 0, 4) };
        root.Controls.Add(lblLog, 0, 2);

        _log.Dock = DockStyle.Fill;
        root.Controls.Add(_log, 0, 3);

        return root;
    }

    private GroupBox BuildSingleGroup()
    {
        var gb = new GroupBox { Text = "단일 생성", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10) };
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 4,
            RowCount = 3,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));

        AddRow(grid, 0, "IP",    _txtIp,   "ViaIP", _txtViaIp);
        AddRow(grid, 1, "Slot",  _numSlot, "Base",  _numBase);

        var outPanel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        outPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        outPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _txtOut.Dock = DockStyle.Fill;
        outPanel.Controls.Add(_txtOut, 0, 0);
        outPanel.Controls.Add(_btnOutPick, 1, 0);

        AddRow(grid, 2, "출력", outPanel, "", _btnRunSingle);

        gb.Controls.Add(grid);
        return gb;
    }

    private GroupBox BuildBatchGroup()
    {
        var gb = new GroupBox { Text = "일괄 생성 (CSV)", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10) };
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 4,
            RowCount = 2,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var csvPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        csvPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        csvPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _txtCsv.Dock = DockStyle.Fill;
        csvPanel.Controls.Add(_txtCsv, 0, 0);
        csvPanel.Controls.Add(_btnCsvPick, 1, 0);

        var dirPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
        dirPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        dirPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _txtOutDir.Dock = DockStyle.Fill;
        dirPanel.Controls.Add(_txtOutDir, 0, 0);
        dirPanel.Controls.Add(_btnOutDirPick, 1, 0);

        AddRow(grid, 0, "CSV",   csvPanel, "", _btnCsvSample);
        AddRow(grid, 1, "출력폴더", dirPanel, "", _btnRunBatch);

        gb.Controls.Add(grid);
        return gb;
    }

    private void ExportSampleCsv()
    {
        using var dlg = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv",
            FileName = "xgwx-sample.csv",
            Title = "양식 CSV 저장",
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        // UTF-8 with BOM — Excel 이 한글 CSV 를 올바로 인식하게 함.
        var content =
            "Name,Ip,ViaIp,Slot,Base\r\n" +
            "LINE_EXAMPLE,10.1.2.3,,,\r\n" +
            "ROBOT_EXAMPLE,10.2.3.4,10.143.52.204,6,0\r\n";
        File.WriteAllText(dlg.FileName, content, new System.Text.UTF8Encoding(true));

        Log($"양식 CSV 저장: {dlg.FileName}");
        _txtCsv.Text = dlg.FileName;
        OfferOpenFolder(dlg.FileName);
    }

    private static void AddRow(TableLayoutPanel grid, int row, string l1, Control c1, string l2, Control c2)
    {
        var lbl1 = new Label { Text = l1, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 8, 0) };
        var lbl2 = new Label { Text = l2, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(12, 8, 8, 0) };
        c1.Dock = DockStyle.Fill; c1.Margin = new Padding(0, 4, 0, 4);
        c2.Dock = DockStyle.Fill; c2.Margin = new Padding(0, 4, 0, 4);
        grid.Controls.Add(lbl1, 0, row);
        grid.Controls.Add(c1,   1, row);
        grid.Controls.Add(lbl2, 2, row);
        grid.Controls.Add(c2,   3, row);
    }

    // ── 동작 ────────────────────────────────────────────────────────

    private void RunSingle()
    {
        var ip = _txtIp.Text.Trim();
        var via = _txtViaIp.Text.Trim();
        var outPath = _txtOut.Text.Trim();

        if (string.IsNullOrWhiteSpace(ip)) throw new ArgumentException("IP 를 입력하세요");
        if (string.IsNullOrWhiteSpace(outPath)) throw new ArgumentException("출력 경로를 지정하세요");
        if (!outPath.EndsWith(".xgwx", StringComparison.OrdinalIgnoreCase)) outPath += ".xgwx";

        int? slot = string.IsNullOrWhiteSpace(via) ? null : (int)_numSlot.Value;
        int? baseNo = string.IsNullOrWhiteSpace(via) ? null : (int)_numBase.Value;

        XgwxBuilder.Make(outPath, ip, string.IsNullOrWhiteSpace(via) ? null : via, slot, baseNo);
        Log($"OK  {outPath}");
        OfferOpenFolder(outPath);
    }

    private void RunBatch()
    {
        var csv = _txtCsv.Text.Trim();
        var outDir = _txtOutDir.Text.Trim();
        if (!File.Exists(csv)) throw new FileNotFoundException("CSV 파일 없음", csv);
        if (string.IsNullOrWhiteSpace(outDir))
            outDir = Path.GetDirectoryName(Path.GetFullPath(csv)) ?? ".";
        Directory.CreateDirectory(outDir);

        var rows = XgwxBuilder.ReadCsv(csv);
        int ok = 0, fail = 0;
        foreach (var r in rows)
        {
            var target = Path.Combine(outDir, $"{r.Name}.xgwx");
            try
            {
                XgwxBuilder.Make(target, r.Ip, r.ViaIp, r.Slot, r.Base);
                Log($"OK   {r.Name,-20} -> {target}");
                ok++;
            }
            catch (Exception ex)
            {
                Log($"FAIL {r.Name,-20} : {ex.Message}");
                fail++;
            }
        }
        Log($"--- 총 {ok + fail}, OK {ok}, FAIL {fail}");
        if (ok > 0) OfferOpenFolder(outDir);
    }

    // ── UI 유틸 ─────────────────────────────────────────────────────

    private void SafeRun(Action action)
    {
        try { action(); }
        catch (Exception ex) { Log($"[ERROR] {ex.Message}"); MessageBox.Show(ex.Message, "오류", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private static void PickSaveFile(TextBox target, string filter)
    {
        using var dlg = new SaveFileDialog { Filter = filter, FileName = target.Text };
        if (dlg.ShowDialog() == DialogResult.OK) target.Text = dlg.FileName;
    }

    private static void PickOpenFile(TextBox target, string filter)
    {
        using var dlg = new OpenFileDialog { Filter = filter, FileName = target.Text };
        if (dlg.ShowDialog() == DialogResult.OK) target.Text = dlg.FileName;
    }

    private static void PickFolder(TextBox target)
    {
        using var dlg = new FolderBrowserDialog { SelectedPath = Directory.Exists(target.Text) ? target.Text : "" };
        if (dlg.ShowDialog() == DialogResult.OK) target.Text = dlg.SelectedPath;
    }

    private void OfferOpenFolder(string pathOrFile)
    {
        var dir = File.Exists(pathOrFile) ? Path.GetDirectoryName(pathOrFile) : pathOrFile;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        if (MessageBox.Show($"생성 완료.\n폴더 열까요?\n{dir}", "완료",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}";
        if (InvokeRequired) BeginInvoke(() => _log.AppendText(line));
        else _log.AppendText(line);
    }

}
