using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VkWorkspaceSwitcher
{
    internal sealed class HeaderColorDialog : Form
    {
        private readonly Panel preview = new Panel();
        private readonly Label caption = new Label();
        private readonly FlowLayoutPanel palette = new FlowLayoutPanel();
        private Color chosen;
        internal string SelectedColor { get { return String.Format("#{0:X2}{1:X2}{2:X2}", chosen.R, chosen.G, chosen.B); } }

        internal HeaderColorDialog(Profile profile)
        {
            Theme.Apply(this);
            Text = "Цвет шапки";
            ClientSize = new Size(456, 260);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            chosen = ColorTranslator.FromHtml(ProfileLauncher.EffectiveHeaderColor(profile));
            preview.SetBounds(24, 24, 408, 48);
            caption.Text = profile.Name;
            caption.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
            caption.Dock = DockStyle.Fill;
            caption.Padding = new Padding(14, 0, 14, 0);
            caption.TextAlign = ContentAlignment.MiddleLeft;
            caption.AutoEllipsis = true;
            preview.Controls.Add(caption);
            Controls.Add(preview);

            palette.SetBounds(24, 90, 416, 48);
            palette.WrapContents = false;
            string[] colors = { "#0077FF", "#7C3AED", "#D9468F", "#E5484D", "#F59E0B", "#16A34A", "#0891B2", "#64748B" };
            foreach (string hex in colors)
            {
                ColorSwatch swatch = new ColorSwatch(ColorTranslator.FromHtml(hex)) { Size = new Size(44, 44), Margin = new Padding(0, 0, 8, 0), AccessibleName = hex };
                swatch.Click += delegate { chosen = swatch.SwatchColor; UpdatePreview(); };
                palette.Controls.Add(swatch);
            }
            Controls.Add(palette);
            DarkButton custom = new DarkButton { Text = "Другой цвет…", Location = new Point(24, 150), Size = new Size(152, 36) };
            custom.Click += delegate
            {
                using (ColorDialog picker = new ColorDialog { Color = chosen, FullOpen = true, AnyColor = true })
                    if (picker.ShowDialog(this) == DialogResult.OK) { chosen = Color.FromArgb(picker.Color.R, picker.Color.G, picker.Color.B); UpdatePreview(); }
            };
            Controls.Add(custom);
            DarkButton save = new DarkButton { Text = "Сохранить", Primary = true, Location = new Point(184, 202), Size = new Size(144, 38), DialogResult = DialogResult.OK };
            DarkButton cancel = new DarkButton { Text = "Отмена", Location = new Point(336, 202), Size = new Size(96, 38), DialogResult = DialogResult.Cancel };
            Controls.Add(save);
            Controls.Add(cancel);
            AcceptButton = save;
            CancelButton = cancel;
            UpdatePreview();
        }

        private void UpdatePreview()
        {
            preview.BackColor = caption.BackColor = chosen;
            caption.ForeColor = Theme.ContrastInk(chosen);
            foreach (ColorSwatch swatch in palette.Controls)
            { swatch.Selected = swatch.SwatchColor.ToArgb() == chosen.ToArgb(); swatch.Invalidate(); }
        }
    }

    internal sealed class ColorSwatch : Button
    {
        internal Color SwatchColor { get; private set; }
        internal bool Selected;
        internal ColorSwatch(Color color)
        {
            SwatchColor = color;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent.BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath shape = Theme.Rounded(new Rectangle(2, 2, Width - 5, Height - 5), 9))
            using (Brush fill = new SolidBrush(SwatchColor))
            using (Pen edge = new Pen(Selected || Focused ? Theme.Text : SwatchColor, 2))
            { e.Graphics.FillPath(fill, shape); e.Graphics.DrawPath(edge, shape); }
            if (Selected)
                using (Pen check = new Pen(Theme.ContrastInk(SwatchColor), 2.3F))
                    e.Graphics.DrawLines(check, new[] { new Point(14, 22), new Point(20, 28), new Point(31, 15) });
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }
}
