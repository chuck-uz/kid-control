using System.Diagnostics.CodeAnalysis;

namespace KidControl.Installer.Controls;

public sealed class ModernTextBox : UserControl
{
    private readonly TextBox _textBox = new();
    private bool _focused;

    /// <summary>Matches <see cref="Control.Text"/> nullability (setter accepts null).</summary>
    [AllowNull]
    public override string Text
    {
        get => _textBox.Text;
        set => _textBox.Text = value ?? string.Empty;
    }

    public ModernTextBox()
    {
        Height = 42;
        BackColor = Color.Transparent;
        DoubleBuffered = true;

        _textBox.BorderStyle = BorderStyle.None;
        _textBox.Font = new Font(InstallerFonts.MessageFontFamily, 11f, FontStyle.Regular);
        _textBox.BackColor = Color.FromArgb(248, 248, 248);
        _textBox.ForeColor = Color.Black;
        _textBox.Left = 10;
        _textBox.Top = 10;
        _textBox.Width = Width - 20;
        _textBox.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;

        _textBox.Enter += (_, _) => { _focused = true; Invalidate(); };
        _textBox.Leave += (_, _) => { _focused = false; Invalidate(); };
        _textBox.TextChanged += (_, _) => OnTextChanged(EventArgs.Empty);
        Controls.Add(_textBox);
        Resize += (_, _) => _textBox.Width = Math.Max(10, Width - 20);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(Parent?.BackColor ?? Color.White);

        using var fill = new SolidBrush(Color.FromArgb(245, 245, 245));
        e.Graphics.FillRectangle(fill, 0, 0, Width, Height - 2);

        using var line = new Pen(_focused ? Color.FromArgb(0, 120, 212) : Color.FromArgb(180, 180, 180), _focused ? 2 : 1);
        e.Graphics.DrawLine(line, 0, Height - 2, Width, Height - 2);
    }
}
