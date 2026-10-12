using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace KDriveProgress
{
    /// <summary>Application de zone de notification : icône avec anneau de progression, infobulle, notifications, fenêtre à la demande.</summary>
    sealed class TrayContext : ApplicationContext
    {
        static readonly long[] Thresholds = { 0, 10L << 20, 100L << 20, 1L << 30 };

        readonly Engine _engine;
        readonly Settings _settings = Settings.Load();
        readonly DailyStats _today;
        readonly MainForm _form;
        readonly NotifyIcon _tray;
        readonly Timer _timer;
        IntPtr _iconHandle;
        Icon _icon;
        string _iconKey;
        ViewState _last;
        bool _promptDone, _dialogOpen, _quitting;
        DateTime _relaunchUntil;          // après « Activer automatiquement » : relancer kDrive dès qu'il est quitté
        string _healthNotifiedFor;

        public TrayContext(bool startHidden)
        {
            _engine = new Engine(LogTail.DefaultDirectory, new HistoryStore());
            _engine.Start();

            List<TransferView> seed;
            lock (_engine.Tracker.Sync) seed = _engine.Tracker.HistoryViews(Tracker.HistoryMax);
            _today = DailyStats.Load(seed);

            _form = new MainForm(ClearHistory, BuildOptionsMenu, OnNotice) { TopMost = _settings.TopMost };
            using (var bmp = Icons.Draw(32, IconState.Idle, 0)) _form.Icon = Icons.ToIcon(bmp, out _);

            var menu = new ContextMenuStrip();
            menu.Items.Add(L.T("menu.show"), null, (s, e) => ShowForm()).Font = new Font(menu.Font, FontStyle.Bold);
            menu.Items.Add(new ToolStripSeparator());
            AddOptionItems(menu.Items);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(L.T("menu.quit"), null, (s, e) => Quit());

            _tray = new NotifyIcon { Text = "kDrive Progress", ContextMenuStrip = menu };
            _tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Toggle(); };
            _tray.BalloonTipClicked += (s, e) => ShowForm();
            UpdateTray(null);
            _tray.Visible = true;

            _timer = new Timer { Interval = 500 };
            _timer.Tick += (s, e) => Refresh();
            _timer.Start();

            if (!startHidden) ShowForm();
        }

        // ------------------------------------------------------------------ options

        ContextMenuStrip BuildOptionsMenu()
        {
            var menu = new ContextMenuStrip();
            AddOptionItems(menu.Items);
            return menu;
        }

        /// <summary>Options communes au menu de l'icône et au bouton « Options » de la fenêtre.</summary>
        void AddOptionItems(ToolStripItemCollection items)
        {
            var notify = new ToolStripMenuItem(L.T("menu.notifications"));
            var enabled = new ToolStripMenuItem(L.T("menu.notify")) { CheckOnClick = true };
            enabled.Click += (s, e) => { _settings.Notify = enabled.Checked; _settings.Save(); };
            notify.DropDownItems.Add(enabled);
            notify.DropDownItems.Add(new ToolStripSeparator());
            var sizeItems = new List<ToolStripMenuItem>();
            foreach (var t in Thresholds)
            {
                var label = t == 0 ? L.T("menu.allFiles") : L.T("menu.filesOver", Format.RoundSize(t));
                var item = new ToolStripMenuItem(label) { Tag = t };
                item.Click += (s, e) => { _settings.NotifyMinBytes = (long)item.Tag; _settings.Save(); };
                sizeItems.Add(item);
                notify.DropDownItems.Add(item);
            }
            notify.DropDownItems.Add(new ToolStripSeparator());
            var errors = new ToolStripMenuItem(L.T("menu.notifyErrors")) { CheckOnClick = true };
            errors.Click += (s, e) => { _settings.NotifyErrors = errors.Checked; _settings.Save(); };
            notify.DropDownItems.Add(errors);
            notify.DropDownOpening += (s, e) =>
            {
                enabled.Checked = _settings.Notify;
                errors.Checked = _settings.NotifyErrors;
                foreach (var i in sizeItems)
                {
                    i.Checked = (long)i.Tag == _settings.NotifyMinBytes;
                    i.Enabled = _settings.Notify;
                }
            };
            items.Add(notify);

            var small = new ToolStripMenuItem(L.T("menu.hideSmall")) { CheckOnClick = true, Checked = _settings.HideSmall };
            small.Click += (s, e) => { _settings.HideSmall = small.Checked; _settings.Save(); Refresh(); };
            items.Add(small);
            var top = new ToolStripMenuItem(L.T("menu.topmost")) { CheckOnClick = true, Checked = _settings.TopMost };
            top.Click += (s, e) => { _settings.TopMost = top.Checked; _settings.Save(); _form.TopMost = top.Checked; };
            items.Add(top);

            items.Add(L.T("menu.extended"), null, (s, e) => ShowExtendedDialog());

            var auto = new ToolStripMenuItem(L.T("menu.autostart")) { CheckOnClick = true };
            auto.Click += (s, e) =>
            {
                try { Autostart.Enabled = auto.Checked; }
                catch (Exception ex) { MessageBox.Show(L.T("err.autostart", ex.Message), "kDrive Progress"); }
            };
            items.Add(auto);
            items.Add(L.T("menu.logs"), null, (s, e) =>
            {
                try { Process.Start("explorer.exe", "\"" + LogTail.DefaultDirectory + "\""); } catch (Exception) { }
            });
            items.Add(new ToolStripSeparator());
            var v = typeof(TrayContext).Assembly.GetName().Version;
            items.Add(new ToolStripMenuItem("kDrive Progress " + v.ToString(3)) { Enabled = false });

            auto.Checked = Autostart.Enabled;
            // Le même réglage existe dans deux menus (icône et fenêtre) : on resynchronise à l'ouverture.
            if (items[0].Owner is ToolStripDropDown owner)
                owner.Opening += (s, e) =>
                {
                    auto.Checked = Autostart.Enabled;
                    small.Checked = _settings.HideSmall;
                    top.Checked = _settings.TopMost;
                };
        }

        // ------------------------------------------------------------------ fenêtre

        public void ShowForm()
        {
            _form.PlaceNearTray();
            Refresh();
            _form.Show();
            if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
            _form.Activate();
        }

        void Toggle()
        {
            if (_form.Visible && _form.WindowState != FormWindowState.Minimized) _form.Hide();
            else ShowForm();
        }

        void Refresh()
        {
            var s = ViewState.Capture(_engine, _form.Visible, _settings.MinVisibleBytes, _settings.ExtendedDeclined);
            _last = s;
            bool changed = _today.Roll();
            foreach (var v in s.Finished) changed |= _today.Add(v);
            if (changed) _today.Save();
            UpdateTray(s);
            if (_form.Visible) _form.UpdateView(s, _today.Text);
            Notify(s.Finished);
            CheckLogHealth(s);
            CheckExtendedLog(s);
        }

        // ------------------------------------------------------------------ log étendu et santé du log

        /// <summary>Au lancement, si kDrive tourne sans log étendu : on propose de l'activer (une fois, sauf refus définitif).</summary>
        void CheckExtendedLog(ViewState s)
        {
            if (_relaunchUntil > DateTime.Now && !s.KDriveRunning && s.VariableSet)
            {
                // L'utilisateur vient de quitter kDrive pour appliquer la variable : on le relance.
                _relaunchUntil = default;
                if (ExtendedLog.StartKDrive(_engine.KDriveExe))
                    _tray.ShowBalloonTip(5000, L.T("notif.relaunched.title"), L.T("notif.relaunched"), ToolTipIcon.Info);
            }
            if (_promptDone || !s.Ready || !s.LogFound || !s.KDriveRunning || s.Mode != LogMode.Debug) return;
            _promptDone = true;
            if (s.VariableSet || _settings.ExtendedDeclined) return;
            // Après le passage en cours (la fenêtre principale n'existe peut-être pas encore)
            System.Threading.SynchronizationContext.Current?.Post(_ => ShowExtendedDialog(), null);
        }

        void ShowExtendedDialog()
        {
            if (_dialogOpen) return;
            _dialogOpen = true;
            try
            {
                var s = _last;
                bool active = s != null && s.Mode == LogMode.Extended;
                ExtendedChoice choice;
                using (var dlg = new ExtendedLogDialog(ExtendedLog.VariableSet, active) { TopMost = true })
                {
                    dlg.ShowDialog();
                    choice = dlg.Choice;
                }
                switch (choice)
                {
                    case ExtendedChoice.Automatic:
                        try { ExtendedLog.SetVariable(true); }
                        catch (Exception ex)
                        {
                            MessageBox.Show(L.T("err.setvar", ex.Message), "kDrive Progress", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        _settings.ExtendedDeclined = false;
                        _settings.Save();
                        _relaunchUntil = DateTime.Now.AddMinutes(15);
                        MessageBox.Show(
                            L.T("ext.done"),
                            "kDrive Progress", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        break;
                    case ExtendedChoice.Never:
                        _settings.ExtendedDeclined = true;
                        _settings.Save();
                        break;
                    case ExtendedChoice.RemoveVariable:
                        try { ExtendedLog.SetVariable(false); } catch (Exception) { }
                        _relaunchUntil = default;
                        break;
                }
            }
            finally { _dialogOpen = false; }
            Refresh();
        }

        /// <summary>Prévient une fois par fichier de log si des informations attendues ont disparu (mise à jour de kDrive).</summary>
        void CheckLogHealth(ViewState s)
        {
            if (!s.Ready || s.Health == null || s.Health.Problems.Count == 0 || _healthNotifiedFor == s.LogName) return;
            _healthNotifiedFor = s.LogName;
            _tray.ShowBalloonTip(10000, L.T("notif.health.title"),
                L.T("notif.health", s.Health.KDriveVersion != null ? " " + s.Health.KDriveVersion : ""), ToolTipIcon.Warning);
        }

        void OnNotice(NoticeAction action)
        {
            switch (action)
            {
                case NoticeAction.EnableExtended: ShowExtendedDialog(); break;
                case NoticeAction.StartKDrive:
                    if (!ExtendedLog.StartKDrive(_engine.KDriveExe))
                        MessageBox.Show(L.T("err.nokdrive"), "kDrive Progress", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    break;
                case NoticeAction.HealthDetails:
                    if (_last != null)
                        MessageBox.Show(_form, _last.HealthReport(), L.T("dlg.health.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    break;
            }
        }

        // ------------------------------------------------------------------ notifications

        void Notify(List<TransferView> finished)
        {
            if (finished == null || finished.Count == 0 || !_settings.Notify) return;
            // Inutile de notifier ce que l'on est en train de regarder.
            if (_form.Visible && Form.ActiveForm == _form) return;

            var ok = finished.Where(v => v.Status == "OK" && v.Total >= _settings.NotifyMinBytes).ToList();
            var failed = _settings.NotifyErrors
                ? finished.Where(v => v.Status != "OK" && !Statuses.IsInterrupted(v.Status)).ToList()
                : new List<TransferView>();
            if (ok.Count + failed.Count == 0) return;

            string title, text;
            var icon = ToolTipIcon.Info;
            if (failed.Count > 0)
            {
                icon = ToolTipIcon.Warning;
                title = failed.Count == 1
                    ? L.T(failed[0].Dir == Direction.Up ? "notif.up.failed" : "notif.down.failed")
                    : L.T("notif.failed.many", failed.Count);
                text = Lines(failed.Concat(ok).ToList(), v => v.Status == Statuses.Ok ? Format.Size(v.Total) : L.Status(v.Status));
            }
            else if (ok.Count == 1)
            {
                var v = ok[0];
                title = L.T(v.Dir == Direction.Up ? "notif.up.done" : "notif.down.done");
                double dur = (v.End - v.Start).TotalSeconds;
                text = v.Name + "\n" + L.T("notif.detail", Format.Size(v.Total), Format.Duration(dur))
                       + (v.Rate > 0 ? "  ·  " + Format.Rate(v.Rate.Value) : "");
            }
            else
            {
                title = L.T("notif.done.many", ok.Count);
                text = Lines(ok, v => Format.Size(v.Total));
            }
            if (text.Length > 250) text = text.Substring(0, 249) + "…";
            _tray.ShowBalloonTip(6000, title, text, icon);
        }

        static string Lines(List<TransferView> items, Func<TransferView, string> detail)
        {
            var lines = items.Take(4).Select(v => (v.Dir == Direction.Up ? "↑ " : "↓ ") + v.Name + " (" + detail(v) + ")").ToList();
            if (items.Count > 4) lines.Add(L.T("notif.more", items.Count - 4));
            return string.Join("\n", lines);
        }

        // ------------------------------------------------------------------ icône

        void UpdateTray(ViewState s)
        {
            IconState state;
            double fraction = 0;
            string text;
            if (s == null || !s.Ready) { state = IconState.Idle; text = L.T("tray.starting"); }
            else if (!s.LogFound || !s.KDriveRunning)
            {
                state = IconState.Warning;
                text = L.T(s.LogFound ? "tray.notrunning" : "tray.nolog");
            }
            else if (s.Active.Count == 0)
            {
                state = s.EngineWarning ? IconState.Warning : IconState.Idle;
                var engine = s.EngineText;
                text = "kDrive · " + (engine != null ? char.ToLower(engine[0]) + engine.Substring(1) : L.T("tray.notransfer"));
            }
            else
            {
                state = IconState.Active;
                fraction = s.Fraction ?? 0;
                text = "kDrive · ↑" + s.Up + " ↓" + s.Down;
                if (s.Fraction.HasValue) text += " · " + Format.Percent(s.Fraction.Value);
                if (s.Rate > 0) text += " · " + Format.Rate(s.Rate);
            }
            if (text.Length > 63) text = text.Substring(0, 62) + "…"; // limite de NotifyIcon.Text
            if (_tray.Text != text) _tray.Text = text;

            var key = state + "|" + (int)(fraction * 24);
            if (key == _iconKey) return;
            _iconKey = key;
            var size = SystemInformation.SmallIconSize.Width;
            var oldIcon = _icon;
            var oldHandle = _iconHandle;
            using (var bmp = Icons.Draw(Math.Max(16, size), state, fraction)) _icon = Icons.ToIcon(bmp, out _iconHandle);
            _tray.Icon = _icon;
            oldIcon?.Dispose();
            if (oldHandle != IntPtr.Zero) Native.DestroyIcon(oldHandle);
        }

        void ClearHistory()
        {
            lock (_engine.Tracker.Sync) _engine.Tracker.ClearHistory();
            Refresh();
        }

        public void Quit()
        {
            if (_quitting) return; // menu « Quitter » et --quit peuvent arriver ensemble
            _quitting = true;
            _timer.Stop();
            _tray.Visible = false;
            _engine.Dispose();
            _form.CloseForGood();
            _tray.Dispose();
            if (_iconHandle != IntPtr.Zero) Native.DestroyIcon(_iconHandle);
            ExitThread();
        }
    }
}
