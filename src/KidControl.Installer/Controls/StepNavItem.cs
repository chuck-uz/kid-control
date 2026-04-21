namespace KidControl.Installer.Controls;

public sealed class StepNavItem : Label
{
    private bool _isActive;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            ForeColor = value ? Color.White : Color.FromArgb(190, 210, 235);
            Font = new Font(InstallerFonts.MessageFontFamily, value ? 10.5f : 10f, value ? FontStyle.Bold : FontStyle.Regular);
            Invalidate();
        }
    }

    public StepNavItem()
    {
        AutoSize = false;
        Height = 38;
        Width = 200;
        Padding = new Padding(12, 10, 6, 10);
        ForeColor = Color.FromArgb(190, 210, 235);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (IsActive)
        {
            using var brush = new SolidBrush(Color.FromArgb(60, 255, 255, 255));
            e.Graphics.FillRectangle(brush, 0, 0, Width, Height);
        }
        base.OnPaint(e);
    }
}
