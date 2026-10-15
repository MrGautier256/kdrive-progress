using System;
using System.Drawing;
using System.Windows.Forms;

namespace KDriveProgress
{
    /// <summary>Fenêtre compacte : transferts en cours en haut, terminés récemment en bas.</summary>
    sealed class MainForm : Form
    {
        readonly Label _summary = new Label();
        readonly Label _today = new Label();
        readonly Label _quota = new Label();
        readonly Label _engineState = new Label();
        readonly Label _histTitle = new Label();
        readonly LinkLabel _errorsLink = new LinkLabel();
        readonly LinkLabel _clear = new LinkLabel();
        bool _showErrors;   // la liste du bas montre les erreurs kDrive au lieu de l'historique
        readonly Label _status = new Label();
        readonly ActiveView _active = new ActiveView { Dock = DockStyle.Fill, EmptyText = L.T("empty.active") };
        readonly HistoryView _history = new HistoryView { Dock = DockStyle.Fill, EmptyText = L.T("empty.history") };
        readonly Action _clearHistory;
        readonly ContextMenuStrip _options;
        readonly LinkLabel _action = new LinkLabel();
        NoticeAction _currentAction;
        bool _allowClose;
        bool _placed;

        public MainForm(Action clearHistory, Func<ContextMenuStrip> buildOptions, Action<NoticeAction> onAction)
        {
            _clearHistory = clearHistory;
            _options = buildOptions();
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = L.T("form.title");
            Font = new Font("Segoe UI", 9f);
            BackColor = Theme.Window;
            ForeColor = Theme.Text;
            ClientSize = new Size(500, 560);
            MinimumSize = new Size(360, 320);
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = true;
            KeyPreview = true;

            // --- en-tête
            var header = new Panel { Dock = DockStyle.Top, Height = 104, Padding = new Padding(14, 10, 14, 6), BackColor = Theme.Window };
            var titleRow = new Panel { Dock = DockStyle.Top, Height = 26, BackColor = Theme.Window };
            var title = new Label
            {
                Text = L.T("header.title"), Dock = DockStyle.Fill, AutoSize = false,
                Font = new Font("Segoe UI Semibold", 11.5f), ForeColor = Theme.Text,
            };
            _engineState.Dock = DockStyle.Right;
            _engineState.AutoSize = true;
            _engineState.Padding = new Padding(0, 4, 0, 0);
            _engineState.ForeColor = Theme.Muted;
            titleRow.Controls.Add(title);
            titleRow.Controls.Add(_engineState);
            _summary.Dock = DockStyle.Top;
            _summary.Height = 20;
            _summary.ForeColor = Theme.Text;
            _summary.Text = "…";
            _today.Dock = DockStyle.Top;
            _today.Height = 18;
            _today.ForeColor = Theme.Muted;
            _today.Font = new Font("Segoe UI", 8.25f);
            _quota.Dock = DockStyle.Top;
            _quota.Height = 18;
            _quota.ForeColor = Theme.Muted;
            _quota.Font = new Font("Segoe UI", 8.25f);
            header.Controls.Add(_quota);
            header.Controls.Add(_today);
            header.Controls.Add(_summary);
            header.Controls.Add(titleRow);

            // --- historique : bandeau + liste
            var histHeader = new Panel { Dock = DockStyle.Top, Height = 28, BackColor = Theme.Window, Padding = new Padding(14, 6, 14, 2) };
            _histTitle.Text = L.T("history.title");
            _histTitle.Dock = DockStyle.Fill;
            _histTitle.Font = new Font("Segoe UI Semibold", 9f);
            _histTitle.ForeColor = Theme.Text;
            _clear.Text = L.T("history.clear");
            _clear.Dock = DockStyle.Right;
            _clear.AutoSize = true;
            _clear.LinkColor = _clear.ActiveLinkColor = Theme.Up;
            _clear.LinkBehavior = LinkBehavior.HoverUnderline;
            _clear.LinkClicked += (s, e) => _clearHistory();
            // Bascule entre l'historique et la liste des erreurs tenue par kDrive (kDrive 4)
            _errorsLink.Dock = DockStyle.Right;
            _errorsLink.AutoSize = true;
            _errorsLink.Padding = new Padding(0, 0, 12, 0);
            _errorsLink.LinkColor = _errorsLink.ActiveLinkColor = Theme.Warning;
            _errorsLink.LinkBehavior = LinkBehavior.HoverUnderline;
            _errorsLink.Visible = false;
            _errorsLink.LinkClicked += (s, e) => { _showErrors = !_showErrors; _history.AutoScrollPosition = Point.Empty; ErrorsToggled?.Invoke(); };
            histHeader.Controls.Add(_histTitle);
            histHeader.Controls.Add(_errorsLink);
            histHeader.Controls.Add(_clear);

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, BackColor = Theme.Window,
                SplitterWidth = 6, Panel1MinSize = 70, Panel2MinSize = 60, FixedPanel = FixedPanel.None, TabStop = false,
            };
            split.Panel1.Controls.Add(_active);
            split.Panel2.Controls.Add(_history);
            split.Panel2.Controls.Add(histHeader);

            // --- pied : état de la source et options
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(14, 6, 14, 6), BackColor = Theme.Window };
            _status.Dock = DockStyle.Fill;
            _status.ForeColor = Theme.Muted;
            _status.Font = new Font("Segoe UI", 8.25f);
            _status.TextAlign = ContentAlignment.MiddleLeft;
            var options = new LinkLabel
            {
                Text = L.T("options"), Dock = DockStyle.Right, AutoSize = true, Padding = new Padding(10, 0, 0, 0),
                TextAlign = ContentAlignment.MiddleRight, LinkColor = Theme.Up, ActiveLinkColor = Theme.Up,
                LinkBehavior = LinkBehavior.HoverUnderline,
            };
            options.LinkClicked += (s, e) => _options.Show(options, new Point(0, options.Height), ToolStripDropDownDirection.AboveRight);
            _action.Dock = DockStyle.Right;
            _action.AutoSize = true;
            _action.Padding = new Padding(8, 0, 0, 0);
            _action.TextAlign = ContentAlignment.MiddleRight;
            _action.LinkColor = _action.ActiveLinkColor = Theme.Up;
            _action.LinkBehavior = LinkBehavior.HoverUnderline;
            _action.Visible = false;
            _action.LinkClicked += (s, e) => onAction(_currentAction);
            footer.Controls.Add(_status);
            footer.Controls.Add(_action);
            footer.Controls.Add(options);

            Controls.Add(split);
            Controls.Add(footer);
            Controls.Add(header);
            ResumeLayout(true);

            Load += (s, e) => split.SplitterDistance = (int)(split.Height * 0.58);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.ApplyTitleBar(Handle);
        }

        /// <summary>Première ouverture : en bas à droite, près de la zone de notification.</summary>
        public void PlaceNearTray()
        {
            if (_placed) return;
            _placed = true;
            var wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            int margin = LogicalToDeviceUnits(12);
            Location = new Point(wa.Right - Width - margin, wa.Bottom - Height - margin);
        }

        public void UpdateView(ViewState s, string todayText)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (s.Up > 0) parts.Add(L.T(s.Up > 1 ? "summary.up.many" : "summary.up.one", s.Up));
            if (s.Down > 0) parts.Add(L.T(s.Down > 1 ? "summary.down.many" : "summary.down.one", s.Down));
            if (s.Fraction.HasValue) parts.Add(Format.Percent(s.Fraction.Value));
            if (s.Rate > 0) parts.Add(Format.Rate(s.Rate));
            if (s.Hidden > 0) parts.Add(L.T(s.Hidden > 1 ? "summary.hidden.many" : "summary.hidden.one", s.Hidden));
            _summary.Text = parts.Count == 0 ? L.T("summary.none") : string.Join("   ·   ", parts);
            _today.Text = todayText;
            var engine = s.EngineText;
            _engineState.Text = engine == null ? "" : "● " + engine;
            _engineState.ForeColor = s.EngineWarning ? Theme.Warning
                : s.Engine?.State == EngineState.UpToDate ? Theme.Ok
                : s.Engine?.State == EngineState.Syncing || s.Engine?.State == EngineState.Scanning ? Theme.Up : Theme.Muted;
            _active.EmptyText = L.T(s.Hidden > 0 ? "empty.active.hidden" : "empty.active");
            _status.Text = s.StatusText;
            _currentAction = s.Action;
            _action.Visible = s.Action != NoticeAction.None;
            if (_action.Visible && _action.Text != s.ActionText) _action.Text = s.ActionText;
            _status.ForeColor = s.Warning ? Theme.Warning : Theme.Muted;
            if (s.UpdateAvailable && !s.Warning) _status.ForeColor = Theme.Up;
            _active.SetItems(s.Active);

            // Espace du kDrive (kDrive 4) : la ligne disparaît si l'information n'est pas connue
            var quota = s.QuotaText;
            _quota.Visible = quota != null;
            if (quota != null) _quota.Text = quota;

            // Liste du bas : historique, ou erreurs tenues par kDrive
            if (s.KDriveErrorCount == 0) _showErrors = false;
            _errorsLink.Visible = s.KDriveErrorCount > 0;
            _errorsLink.Text = _showErrors ? L.T("history.title") : "⚠ " + L.T("history.errors", s.KDriveErrorCount);
            _errorsLink.LinkColor = _errorsLink.ActiveLinkColor = _showErrors ? Theme.Up : Theme.Warning;
            _histTitle.Text = _showErrors ? L.T("errors.title") : L.T("history.title");
            _clear.Visible = !_showErrors;
            _history.EmptyText = L.T(_showErrors ? "empty.errors" : "empty.history");
            if (_showErrors) { if (s.KDriveErrors != null) _history.SetItems(s.KDriveErrors); }
            else if (s.History != null) _history.SetItems(s.History);
        }

        /// <summary>Vrai quand la liste du bas affiche les erreurs kDrive.</summary>
        public bool ShowErrors => _showErrors;

        /// <summary>L'utilisateur a basculé entre historique et erreurs : l'interface doit se rafraîchir.</summary>
        public event Action ErrorsToggled;

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape) Hide();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // La croix masque la fenêtre : l'app reste dans la zone de notification.
            if (!_allowClose && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
            base.OnFormClosing(e);
        }

        public void CloseForGood()
        {
            _allowClose = true;
            Close();
        }
    }
}
