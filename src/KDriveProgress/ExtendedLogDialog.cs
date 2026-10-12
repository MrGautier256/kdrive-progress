using System;
using System.Drawing;
using System.Windows.Forms;

namespace KDriveProgress
{
    enum ExtendedChoice { None, Automatic, Manual, Never, RemoveVariable }

    /// <summary>Propose d'activer le log étendu de kDrive, automatiquement ou à la main.</summary>
    sealed class ExtendedLogDialog : Form
    {
        static readonly Color PrimaryBlue = Color.FromArgb(0x25, 0x63, 0xEB);

        public ExtendedChoice Choice { get; private set; }

        public ExtendedLogDialog(bool variableSet, bool extendedActive)
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "kDrive Progress";
            Font = new Font("Segoe UI", 9f);
            BackColor = Theme.Window;
            ForeColor = Theme.Text;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            ClientSize = new Size(600, 10);
            Padding = new Padding(20, 16, 20, 16);

            var title = new Label
            {
                Text = L.T(extendedActive ? "ext.title.active" : "ext.title.ask"),
                Font = new Font("Segoe UI Semibold", 12f), AutoSize = true, MaximumSize = new Size(560, 0), Dock = DockStyle.Top,
            };

            string body;
            if (extendedActive)
                body = L.T("ext.body.active") + (variableSet ? "\n\n" + L.T("ext.body.active.var", ExtendedLog.Variable) : "");
            else if (variableSet)
                body = L.T("ext.body.pending", ExtendedLog.Variable);
            else
                body = L.T("ext.body.ask", ExtendedLog.Variable);
            var text = new Label
            {
                Text = body, AutoSize = true, MaximumSize = new Size(560, 0), Dock = DockStyle.Top,
                Padding = new Padding(0, 10, 0, 14), ForeColor = Theme.Text,
            };

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, WrapContents = true,
                Padding = new Padding(0, 4, 0, 0), BackColor = Theme.Window,
            };
            Button Add(string label, ExtendedChoice choice, bool primary = false)
            {
                var b = new Button
                {
                    Text = label, AutoSize = true, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(8, 0, 0, 6),
                    FlatStyle = FlatStyle.Flat, BackColor = primary ? PrimaryBlue : Theme.Surface,
                    ForeColor = primary ? Color.White : Theme.Text, UseVisualStyleBackColor = false,
                };
                b.FlatAppearance.BorderColor = primary ? PrimaryBlue : Theme.Border;
                b.Click += (s, e) => { Choice = choice; Close(); };
                buttons.Controls.Add(b);
                return b;
            }

            if (extendedActive || variableSet)
            {
                var ok = Add(L.T("ext.btn.ok"), ExtendedChoice.None, primary: true);
                if (variableSet) Add(L.T("ext.btn.remove"), ExtendedChoice.RemoveVariable);
                AcceptButton = ok;
            }
            else
            {
                var auto = Add(L.T("ext.btn.auto"), ExtendedChoice.Automatic, primary: true);
                Add(L.T("ext.btn.manual"), ExtendedChoice.Manual);
                Add(L.T("ext.btn.never"), ExtendedChoice.Never);
                AcceptButton = auto;
            }

            Controls.Add(buttons);
            Controls.Add(text);
            Controls.Add(title);
            ResumeLayout(true);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Hauteur ajustée au texte
            int h = Padding.Vertical;
            foreach (Control c in Controls) h += c.Height;
            ClientSize = new Size(ClientSize.Width, h);
            CenterToScreen();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyTitleBar(Handle);
        }
    }
}
