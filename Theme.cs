using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VkWorkspaceSwitcher
{
    internal static class Theme
    {
        internal static readonly Color Background = Color.FromArgb(25, 25, 26);
        internal static readonly Color Surface = Color.FromArgb(35, 35, 36);
        internal static readonly Color Hover = Color.FromArgb(47, 48, 50);
        internal static readonly Color Border = Color.FromArgb(58, 60, 63);
        internal static readonly Color Text = Color.FromArgb(225, 227, 230);
        internal static readonly Color Muted = Color.FromArgb(144, 148, 153);
        internal static readonly Color Blue = Color.FromArgb(0, 119, 255);
        internal static readonly Color Selection = Color.FromArgb(34, 53, 75);
        internal static readonly Color Green = Color.FromArgb(75, 179, 113);
        internal static readonly Icon AppIcon = LoadIcon();

        internal static Color ContrastInk(Color color)
        {
            return color.R * 299 + color.G * 587 + color.B * 114 >= 155000 ? Color.FromArgb(16, 19, 24) : Color.White;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        private static Icon LoadIcon()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("VkWorkspaceSwitcher.AppIcon"))
            {
                if (stream == null) return (Icon)SystemIcons.Application.Clone();
                using (Icon icon = new Icon(stream, 48, 48)) return (Icon)icon.Clone();
            }
        }

        internal static void Apply(Form form)
        {
            form.Font = new Font("Segoe UI", 10F);
            form.BackColor = Background;
            form.ForeColor = Text;
            form.Icon = AppIcon;
            form.HandleCreated += delegate
            {
                try
                {
                    int enabled = 1;
                    if (DwmSetWindowAttribute(form.Handle, 20, ref enabled, 4) != 0)
                        DwmSetWindowAttribute(form.Handle, 19, ref enabled, 4);
                }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
            };
        }

        internal static GraphicsPath Rounded(Rectangle rectangle, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
            path.AddArc(rectangle.Left, rectangle.Top, d, d, 180, 90);
            path.AddArc(rectangle.Right - d, rectangle.Top, d, d, 270, 90);
            path.AddArc(rectangle.Right - d, rectangle.Bottom - d, d, d, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        internal static void ShowError(IWin32Window owner, string message)
        {
            using (Form dialog = new Form())
            {
                Apply(dialog);
                dialog.Text = "Не удалось выполнить действие";
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MaximizeBox = dialog.MinimizeBox = false;
                dialog.StartPosition = owner == null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
                int height = Math.Min(300, Math.Max(60, TextRenderer.MeasureText(message, dialog.Font, new Size(420, 0), TextFormatFlags.WordBreak).Height + 12));
                dialog.ClientSize = new Size(468, height + 100);
                TextBox text = new TextBox { Text = message, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None,
                    BackColor = Background, ForeColor = Text, Location = new Point(24, 24), Size = new Size(420, height),
                    ScrollBars = ScrollBars.Vertical, TabStop = false };
                DarkButton close = new DarkButton { Text = "Понятно", Primary = true, DialogResult = DialogResult.OK,
                    Location = new Point(304, height + 44), Size = new Size(140, 36) };
                dialog.Controls.Add(text);
                dialog.Controls.Add(close);
                dialog.AcceptButton = dialog.CancelButton = close;
                dialog.ShowDialog(owner);
            }
        }
    }

    internal sealed class DarkButton : Button
    {
        private bool hovered;
        private bool pressed;
        internal bool Primary { get; set; }

        internal DarkButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand;
            Size = new Size(130, 40);
        }

        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? Theme.Background : Parent.BackColor);
            if (Width < 8 || Height < 8) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = !Enabled ? Theme.Surface : Primary ? (pressed ? Color.FromArgb(0, 91, 204) : hovered ? Color.FromArgb(41, 141, 255) : Theme.Blue) : (hovered || pressed ? Theme.Hover : Theme.Surface);
            using (GraphicsPath shape = Theme.Rounded(new Rectangle(1, 1, Width - 3, Height - 3), 9))
            using (Brush brush = new SolidBrush(fill))
            using (Pen edge = new Pen(Focused && ShowFocusCues ? Color.FromArgb(113, 180, 255) : Primary ? fill : Theme.Border))
            {
                e.Graphics.FillPath(brush, shape);
                e.Graphics.DrawPath(edge, shape);
            }
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? (Primary ? Color.White : Theme.Text) : Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    internal sealed class AccountRow
    {
        internal Profile Profile;
        internal bool Running;
        public override string ToString() { return Profile.Name; }
    }

    internal sealed class AccountList : ListBox
    {
        private readonly Font statusFont = new Font("Segoe UI", 9F);

        internal AccountList()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            ItemHeight = 72;
            BorderStyle = BorderStyle.None;
            IntegralHeight = false;
            BackColor = Theme.Background;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 11F);
            AccessibleName = "Аккаунты";
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            using (Brush background = new SolidBrush(BackColor)) e.Graphics.FillRectangle(background, e.Bounds);
            if (e.Index < 0 || e.Index >= Items.Count || e.Bounds.Width < 100) return;
            AccountRow row = (AccountRow)Items[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            Rectangle bounds = new Rectangle(e.Bounds.X + 1, e.Bounds.Y + 2, e.Bounds.Width - 3, e.Bounds.Height - 8);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath shape = Theme.Rounded(bounds, 10))
            using (Brush fill = new SolidBrush(selected ? Theme.Selection : Theme.Surface))
            using (Pen edge = new Pen(selected ? Color.FromArgb(61, 100, 143) : Theme.Surface))
            {
                e.Graphics.FillPath(fill, shape);
                e.Graphics.DrawPath(edge, shape);
            }
            Rectangle avatar = new Rectangle(bounds.X + 14, bounds.Y + 13, 38, 38);
            using (GraphicsPath shape = Theme.Rounded(avatar, 10))
            using (Brush fill = new SolidBrush(ColorTranslator.FromHtml(ProfileLauncher.EffectiveHeaderColor(row.Profile))))
            using (Brush ink = new SolidBrush(Theme.ContrastInk(ColorTranslator.FromHtml(ProfileLauncher.EffectiveHeaderColor(row.Profile)))))
            {
                e.Graphics.FillPath(fill, shape);
                e.Graphics.FillEllipse(ink, avatar.X + 14, avatar.Y + 8, 10, 10);
                using (GraphicsPath shoulders = Theme.Rounded(new Rectangle(avatar.X + 9, avatar.Y + 21, 20, 11), 5))
                    e.Graphics.FillPath(ink, shoulders);
            }
            int x = bounds.X + 66;
            TextRenderer.DrawText(e.Graphics, row.Profile.Name, Font, new Rectangle(x, bounds.Y + 10, bounds.Width - 84, 24), Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            using (Brush dot = new SolidBrush(row.Running ? Theme.Green : Theme.Muted))
                e.Graphics.FillEllipse(dot, x + 3, bounds.Y + 43, 6, 6);
            TextRenderer.DrawText(e.Graphics, row.Running ? "Открыт" : "Не запущен", statusFont, new Rectangle(x + 15, bounds.Y + 34, bounds.Width - 99, 23), Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) statusFont.Dispose();
            base.Dispose(disposing);
        }
    }

    internal sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Surface; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Surface; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Surface; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Surface; } }
        public override Color MenuItemSelected { get { return Theme.Hover; } }
        public override Color MenuItemBorder { get { return Theme.Hover; } }
        public override Color MenuBorder { get { return Theme.Border; } }
        public override Color SeparatorDark { get { return Theme.Border; } }
        public override Color SeparatorLight { get { return Theme.Surface; } }
    }
}
