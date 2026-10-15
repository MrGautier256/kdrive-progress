using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KDriveProgress
{
    /// <summary>Mise à jour de l'app : vérification périodique, téléchargement, installation au calme.</summary>
    sealed partial class TrayContext
    {
        static readonly TimeSpan CheckEvery = TimeSpan.FromHours(12);
        static readonly TimeSpan MaxWaitForIdle = TimeSpan.FromMinutes(30);

        SynchronizationContext _ui;
        System.Windows.Forms.Timer _updateTimer;
        DateTime _nextCheck;
        UpdateInfo _appUpdate;      // version plus récente trouvée sur GitHub
        string _downloaded;         // exe téléchargé et vérifié, prêt à installer
        DateTime _downloadedAt;
        bool _checking, _downloading, _installing;
        string _notifiedTag;

        void InitUpdates(string updatedFrom)
        {
            _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            if (updatedFrom != null)
                _tray.ShowBalloonTip(6000, L.T("update.app.installed.title"),
                    L.T("update.app.installed", Updater.Current.ToString(3), updatedFrom), ToolTipIcon.Info);
            _nextCheck = DateTime.Now.AddSeconds(30); // laisser l'app démarrer tranquillement
            _updateTimer = new System.Windows.Forms.Timer { Interval = 15000 };
            _updateTimer.Tick += (s, e) => UpdateTick();
            _updateTimer.Start();
        }

        void UpdateTick()
        {
            if (_settings.AutoCheckUpdates && !_checking && DateTime.Now >= _nextCheck) CheckForUpdate(manual: false);
            TryAutoInstall();
        }

        void CheckForUpdate(bool manual)
        {
            if (_checking) return;
            _checking = true;
            _nextCheck = DateTime.Now + CheckEvery;
            Task.Run(() =>
            {
                try { return Tuple.Create(Updater.Check(), (Exception)null); }
                catch (Exception ex) { return Tuple.Create((UpdateInfo)null, ex); }
            }).ContinueWith(t => _ui.Post(_ => OnChecked(t.Result.Item1, t.Result.Item2, manual), null));
        }

        void OnChecked(UpdateInfo info, Exception error, bool manual)
        {
            _checking = false;
            if (_quitting) return;
            if (error != null)
            {
                if (manual) _tray.ShowBalloonTip(6000, "kDrive Progress", L.T("update.app.checkfailed", error.Message), ToolTipIcon.Warning);
                return;
            }
            if (info == null)
            {
                if (manual) _tray.ShowBalloonTip(5000, "kDrive Progress", L.T("update.app.uptodate", Updater.Current.ToString(3)), ToolTipIcon.Info);
                return;
            }
            if (_appUpdate == null || info.Version > _appUpdate.Version) { _appUpdate = info; _downloaded = null; }
            bool auto = _settings.AutoInstallUpdates && Updater.CanReplaceSelf();
            if (_notifiedTag != info.Tag)
            {
                _notifiedTag = info.Tag;
                _tray.ShowBalloonTip(8000, L.T("update.app.available", info.Version.ToString(3)),
                    L.T(auto ? "notif.app.update.auto" : "notif.app.update.manual"), ToolTipIcon.Info);
            }
            if (auto) StartDownload(installAtOnce: false);
            Refresh();
        }

        void StartDownload(bool installAtOnce)
        {
            if (_downloading || _appUpdate == null) return;
            _downloading = true;
            var info = _appUpdate;
            Task.Run(() =>
            {
                try { return Tuple.Create(Updater.Download(info), (Exception)null); }
                catch (Exception ex) { return Tuple.Create((string)null, ex); }
            }).ContinueWith(t => _ui.Post(_ =>
            {
                _downloading = false;
                if (t.Result.Item2 != null)
                {
                    _installing = false;
                    _tray.ShowBalloonTip(6000, "kDrive Progress", L.T("update.app.failed", t.Result.Item2.Message), ToolTipIcon.Warning);
                    Refresh();
                    return;
                }
                _downloaded = t.Result.Item1;
                _downloadedAt = DateTime.Now;
                if (installAtOnce) Apply();
            }, null));
        }

        /// <summary>Installation automatique : quand aucun transfert n'est suivi, ou au plus tard après 30 minutes d'attente.</summary>
        void TryAutoInstall()
        {
            if (_downloaded == null || _installing || _dialogOpen || !_settings.AutoInstallUpdates) return;
            bool idle = _last == null || (_last.Active.Count == 0 && _last.Hidden == 0);
            if (idle || DateTime.Now - _downloadedAt > MaxWaitForIdle) Apply();
        }

        /// <summary>Bouton « Installer » : tout de suite, ou page de la release si l'app ne peut pas se remplacer elle-même.</summary>
        void InstallNow()
        {
            if (_appUpdate == null || _installing) return;
            if (!Updater.CanReplaceSelf()) { Updater.OpenReleasePage(_appUpdate.PageUrl); return; }
            _installing = true;
            Refresh();
            if (_downloaded != null) Apply();
            else StartDownload(installAtOnce: true);
        }

        void Apply()
        {
            _installing = true;
            try
            {
                Updater.Apply(_downloaded, startHidden: !_form.Visible);
            }
            catch (Exception ex)
            {
                _installing = false;
                _downloaded = null;
                _tray.ShowBalloonTip(6000, "kDrive Progress", L.T("update.app.failed", ex.Message), ToolTipIcon.Warning);
                Refresh();
                return;
            }
            Quit(); // la nouvelle version attend la fin de celle-ci pour démarrer
        }
    }
}
