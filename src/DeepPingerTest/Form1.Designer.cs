namespace DeepPingerTest;

partial class Form1
{
    private System.ComponentModel.IContainer components = null;

    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(520, 600);
        Text = "DeepPinger Test";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("맑은 고딕", 9F);

        // --- Input Panel ---
        var panelInput = new Panel
        {
            Location = new Point(12, 12),
            Size = new Size(490, 190),
            BorderStyle = BorderStyle.FixedSingle
        };

        var lblTitle = new Label { Text = "DeepPinger DLL 테스트", Location = new Point(10, 8), AutoSize = true, Font = new Font("맑은 고딕", 11F, FontStyle.Bold) };

        var lblIp = new Label { Text = "IP (핑 대상):", Location = new Point(10, 45), AutoSize = true };
        txtIp = new TextBox { Location = new Point(130, 42), Size = new Size(200, 23), PlaceholderText = "예: 192.168.1.100" };

        var lblViaIp = new Label { Text = "ViaIP (게이트웨이):", Location = new Point(10, 75), AutoSize = true };
        txtViaIp = new TextBox { Location = new Point(130, 72), Size = new Size(200, 23), PlaceholderText = "예: 192.168.1.1" };

        var lblBase = new Label { Text = "Base:", Location = new Point(10, 105), AutoSize = true };
        numBase = new NumericUpDown { Location = new Point(130, 102), Size = new Size(80, 23), Minimum = 0, Maximum = 99, Value = 0 };

        var lblSlot = new Label { Text = "Slot:", Location = new Point(230, 105), AutoSize = true };
        numSlot = new NumericUpDown { Location = new Point(270, 102), Size = new Size(80, 23), Minimum = 0, Maximum = 99, Value = 0 };

        var lblTimeout = new Label { Text = "Timeout (초):", Location = new Point(10, 135), AutoSize = true };
        numTimeout = new NumericUpDown { Location = new Point(130, 132), Size = new Size(80, 23), Minimum = 1, Maximum = 60, Value = 5 };

        btnTest = new Button
        {
            Text = "테스트",
            Location = new Point(350, 42),
            Size = new Size(120, 80),
            Font = new Font("맑은 고딕", 11F, FontStyle.Bold),
            BackColor = Color.FromArgb(0, 120, 215),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand
        };
        btnTest.Click += BtnTest_Click;

        lblStatus = new Label
        {
            Text = "대기 중",
            Location = new Point(130, 162),
            Size = new Size(340, 20),
            ForeColor = Color.Gray
        };

        panelInput.Controls.AddRange([lblTitle, lblIp, txtIp, lblViaIp, txtViaIp, lblBase, numBase, lblSlot, numSlot, lblTimeout, numTimeout, btnTest, lblStatus]);

        // --- Result Panel ---
        var lblResult = new Label { Text = "결과:", Location = new Point(12, 210), AutoSize = true, Font = new Font("맑은 고딕", 9F, FontStyle.Bold) };

        txtResult = new TextBox
        {
            Location = new Point(12, 230),
            Size = new Size(490, 100),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.White,
            Font = new Font("Consolas", 10F)
        };

        // --- Log Panel ---
        var lblLog = new Label { Text = "로그:", Location = new Point(12, 338), AutoSize = true, Font = new Font("맑은 고딕", 9F, FontStyle.Bold) };

        btnClearLog = new Button { Text = "지우기", Location = new Point(450, 334), Size = new Size(52, 22), FlatStyle = FlatStyle.Flat };
        btnClearLog.Click += (s, e) => txtLog.Clear();

        txtLog = new TextBox
        {
            Location = new Point(12, 358),
            Size = new Size(490, 230),
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.FromArgb(200, 200, 200),
            Font = new Font("Consolas", 9F)
        };

        Controls.AddRange([panelInput, lblResult, txtResult, lblLog, btnClearLog, txtLog]);
    }

    #endregion

    private TextBox txtIp;
    private TextBox txtViaIp;
    private NumericUpDown numBase;
    private NumericUpDown numSlot;
    private NumericUpDown numTimeout;
    private Button btnTest;
    private Button btnClearLog;
    private Label lblStatus;
    private TextBox txtResult;
    private TextBox txtLog;
}
