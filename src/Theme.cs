using System;
using System.Drawing;
using System.Windows.Forms;

namespace AfkHub;

static class Theme
{
    public static readonly Color Bg = Color.FromArgb(16, 18, 23);
    public static readonly Color Side = Color.FromArgb(21, 24, 31);
    public static readonly Color Card = Color.FromArgb(27, 31, 40);
    public static readonly Color Raised = Color.FromArgb(37, 42, 54);
    public static readonly Color Border = Color.FromArgb(48, 54, 68);
    public static readonly Color Text = Color.FromArgb(232, 235, 241);
    public static readonly Color Muted = Color.FromArgb(146, 154, 171);
    public static readonly Color Accent = Color.FromArgb(92, 136, 255);
    public static readonly Color AccentHover = Color.FromArgb(116, 156, 255);
    public static readonly Color Good = Color.FromArgb(74, 204, 128);
    public static readonly Color Warn = Color.FromArgb(240, 184, 64);
    public static readonly Color Bad = Color.FromArgb(240, 98, 98);

    public static readonly Font Body = new("Segoe UI", 9.75f);
    public static readonly Font Small = new("Segoe UI", 8.75f);
    public static readonly Font Strong = new("Segoe UI Semibold", 9.75f);
    public static readonly Font H2 = new("Segoe UI Semibold", 11.5f);
    public static readonly Font H1 = new("Segoe UI Semibold", 15f);
    public static readonly Font Big = new("Segoe UI Semibold", 20f);

    public static Color For(Level level) => level switch
    {
        Level.Good => Good,
        Level.Warn => Warn,
        Level.Bad => Bad,
        _ => Text,
    };

    public static Button Button(string text, bool primary = false, EventHandler onClick = null)
    {
        var b = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Accent : Raised,
            ForeColor = primary ? Color.White : Text,
            Font = primary ? Strong : Body,
            Padding = new Padding(10, 4, 10, 4),
            Margin = new Padding(0, 0, 8, 8),
            Cursor = Cursors.Hand,
            UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderColor = primary ? Accent : Border;
        b.FlatAppearance.MouseOverBackColor = primary ? AccentHover : Border;
        b.FlatAppearance.MouseDownBackColor = primary ? Accent : Raised;
        if (onClick != null) b.Click += onClick;
        return b;
    }

    public static Label Label(string text, Font font = null, Color? color = null, Padding? margin = null) => new()
    {
        Text = text,
        AutoSize = true,
        Font = font ?? Body,
        ForeColor = color ?? Text,
        BackColor = Color.Transparent,
        Margin = margin ?? new Padding(0, 0, 0, 6),
    };

    /// <summary>A label that wraps inside its parent's width.</summary>
    public static Label Note(string text, int maxWidth = 760)
    {
        var l = Label(text, Small, Muted);
        l.MaximumSize = new Size(maxWidth, 0);
        return l;
    }

    public static CheckBox Check(string text, bool value, Action<bool> changed)
    {
        var c = new CheckBox
        {
            Text = text,
            Checked = value,
            AutoSize = true,
            ForeColor = Text,
            Font = Body,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 2, 0, 6),
            Cursor = Cursors.Hand,
        };
        c.CheckedChanged += (_, _) => changed(c.Checked);
        return c;
    }

    public static NumericUpDown Number(int min, int max, int value, int width = 70)
    {
        var n = new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Max(min, Math.Min(max, value)),
            Width = width,
            BackColor = Raised,
            ForeColor = Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Body,
            Margin = new Padding(0, 0, 8, 8),
        };
        return n;
    }

    public static TextBox TextBox(int width = 300) => new()
    {
        Width = width,
        BackColor = Raised,
        ForeColor = Text,
        BorderStyle = BorderStyle.FixedSingle,
        Font = Body,
        Margin = new Padding(0, 0, 8, 8),
    };

    public static ComboBox Combo(int width = 220)
    {
        return new ComboBox
        {
            Width = width,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Raised,
            ForeColor = Text,
            Font = Body,
            Margin = new Padding(0, 0, 8, 8),
        };
    }

    public static Panel CardPanel(Control content, Padding? margin = null)
    {
        var p = new BorderPanel
        {
            BackColor = Card,
            Padding = new Padding(16, 14, 16, 10),
            Margin = margin ?? new Padding(0, 0, 0, 12),
            Dock = DockStyle.Fill,
        };
        content.Dock = DockStyle.Fill;
        p.Controls.Add(content);
        return p;
    }

    public static FlowLayoutPanel Row(params Control[] controls)
    {
        var f = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };
        f.Controls.AddRange(controls);
        return f;
    }

    public static FlowLayoutPanel Stack(params Control[] controls)
    {
        var f = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
            Margin = new Padding(0),
        };
        f.Controls.AddRange(controls);
        return f;
    }

    public static DataGridView Grid()
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill,
            BackgroundColor = Card,
            GridColor = Border,
            BorderStyle = BorderStyle.None,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
            EnableHeadersVisualStyles = false,
            RowHeadersVisible = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            MultiSelect = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            Font = Body,
        };
        g.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Card, ForeColor = Muted, Font = Small,
            SelectionBackColor = Card, SelectionForeColor = Muted,
            Padding = new Padding(4, 6, 4, 6),
        };
        g.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Card, ForeColor = Text,
            SelectionBackColor = Raised, SelectionForeColor = Text,
            Padding = new Padding(4, 2, 4, 2),
        };
        g.RowTemplate.Height = 34;
        return g;
    }

    public static void Section(FlowLayoutPanel stack, string title, string subtitle = null)
    {
        stack.Controls.Add(Label(title, H2, Text, new Padding(0, stack.Controls.Count == 0 ? 0 : 14, 0, 2)));
        if (subtitle != null) stack.Controls.Add(Note(subtitle));
    }
}

/// <summary>Panel with a 1px border, for cards.</summary>
sealed class BorderPanel : Panel
{
    public BorderPanel()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}
