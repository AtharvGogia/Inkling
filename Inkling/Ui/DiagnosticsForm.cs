namespace Inkling.Ui;

/// <summary>Shows the timing breakdown of recent requests (no prompt or answer text).</summary>
internal sealed class DiagnosticsForm : Form
{
    private readonly TextBox _text;

    public DiagnosticsForm(Func<string> header)
    {
        Text = "Inkling diagnostics";
        Width = 900;
        Height = 700;
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        _text = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font("Consolas", 9.5f),
            BackColor = SystemColors.Window,
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(6) };
        var close = new Button { Text = "Close", AutoSize = true };
        close.Click += (_, _) => Close();
        var clear = new Button { Text = "Clear", AutoSize = true };
        clear.Click += (_, _) => Diagnostics.Clear();
        var copy = new Button { Text = "Copy", AutoSize = true };
        copy.Click += (_, _) => { try { Clipboard.SetText(_text.Text); } catch { } };
        buttons.Controls.AddRange([close, clear, copy]);
        Controls.Add(_text);
        Controls.Add(buttons);

        void Refresh_()
        {
            var traces = Diagnostics.Snapshot();
            var body = traces.Count == 0
                ? "No requests yet. Use Inkling once and the breakdown appears here."
                : string.Join("\r\n", traces.Select(t => t.Report().ReplaceLineEndings("\r\n")));
            _text.Text = header().ReplaceLineEndings("\r\n") + "\r\n\r\n" + Diagnostics.Summary().ReplaceLineEndings("\r\n") + "\r\n\r\n" + body;
        }
        Action onChange = () => { if (IsHandleCreated) BeginInvoke(Refresh_); };
        Diagnostics.Changed += onChange;
        FormClosed += (_, _) => Diagnostics.Changed -= onChange;
        Load += (_, _) => Refresh_();
    }
}
