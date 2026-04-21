using System.Drawing.Drawing2D;

namespace KidControl.Installer.Controls;

public sealed class AccentButton : Button
{
    private bool _hover;
    private bool _pressed;

    public Color AccentColor { get; set; } = Color.FromArgb(0, 120, 212);

    public AccentButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        ForeColor = Color.White;
        Font = new Font(InstallerFonts.MessageFontFamily, 10.5f, FontStyle.Bold);
        Cursor = Cursors.Hand;
        DoubleBuffered = true;
        Height = 40;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs mevent)
    {
        _pressed = true;
        Invalidate();
        base.OnMouseDown(mevent);
    }

    protected override void OnMouseUp(MouseEventArgs mevent)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(mevent);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        pevent.Graphics.Clear(Parent?.BackColor ?? Color.Transparent);

        var baseColor = AccentColor;
        if (_hover)
        {
            baseColor = ControlPaint.Light(baseColor, 0.1f);
        }
        if (_pressed)
        {
            baseColor = ControlPaint.Dark(baseColor, 0.15f);
        }

        using var path = BuildRoundedRect(ClientRectangle, 10);
        using var brush = new SolidBrush(baseColor);
        pevent.Graphics.FillPath(brush, path);

        using var glow = new Pen(Color.FromArgb(90, Color.White), 1f);
        pevent.Graphics.DrawPath(glow, path);

        TextRenderer.DrawText(
            pevent.Graphics,
            Text,
            Font,
            ClientRectangle,
            ForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private static GraphicsPath BuildRoundedRect(Rectangle rectangle, int radius)
    {
        var diameter = radius * 2;
        var arc = new Rectangle(rectangle.Location, new Size(diameter, diameter));
        var path = new GraphicsPath();

        path.AddArc(arc, 180, 90);
        arc.X = rectangle.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = rectangle.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = rectangle.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}
