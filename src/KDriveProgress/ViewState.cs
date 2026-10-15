using System;
using System.Collections.Generic;
using System.Linq;

namespace KDriveProgress
{
    /// <summary>Action proposée par le message d'état en bas de la fenêtre.</summary>
    enum NoticeAction { None, EnableExtended, StartKDrive, HealthDetails, InstallUpdate }

    /// <summary>Instantané de l'état, pris toutes les 500 ms par l'interface.</summary>
    sealed class ViewState
    {
        public List<TransferView> Active;
        public List<TransferView> History;
        public List<TransferView> Finished;   // terminés depuis l'instantané précédent
        public int Hidden;                    // transferts en cours masqués (petits fichiers)
        public EngineInfo Engine;
        public LogMode Mode;
        public string LogName;
        public bool LogFound, KDriveRunning, Ready;
        public string Error;

        public int Up => Active.Count(v => v.Dir == Direction.Up);
        public int Down => Active.Count(v => v.Dir == Direction.Down);
        public double Rate => Active.Sum(v => v.Rate ?? 0);

        /// <summary>Progression globale (octets) des transferts dont la taille est connue.</summary>
        public double? Fraction
        {
            get
            {
                var known = Active.Where(v => v.Total > 0).ToList();
                if (known.Count == 0) return null;
                return Math.Min(1.0, (double)known.Sum(v => v.Done) / known.Sum(v => v.Total));
            }
        }

        public LogHealth Health;
        public bool VariableSet, ExtendedDeclined;

        // kDrive 4 (messages de l'interface officielle)
        public bool GuiMessages;
        public long QuotaUsed = -1, QuotaTotal = -1;
        public int UpdateState = -1;
        public int KDriveErrorCount;
        public List<TransferView> KDriveErrors;

        /// <summary>Mise à jour de kDrive disponible (2, 3) ou prête à installer (5).</summary>
        public bool UpdateAvailable => UpdateState == 2 || UpdateState == 3 || UpdateState == 5;
        public string UpdateText => UpdateState == 5 ? L.T("update.ready") : L.T("update.available");

        public string QuotaText => QuotaUsed >= 0 && QuotaTotal > 0
            ? L.T("quota", Format.Size(QuotaUsed), Format.Size(QuotaTotal), Format.Percent((double)QuotaUsed / QuotaTotal))
            : null;

        public bool Warning;
        public NoticeAction Action;
        public string ActionText;

        /// <summary>Message d'état du bas de la fenêtre, du plus important au moins important.</summary>
        public string StatusText { get; private set; }

        void ComputeNotice()
        {
            Warning = true;
            Action = NoticeAction.None;
            if (!Ready) { Warning = false; StatusText = L.T("notice.analyzing"); return; }
            if (!LogFound) { StatusText = L.T("notice.nolog"); return; }
            if (Health.Problems.Count > 0)
            {
                StatusText = L.T("notice.health", Health.KDriveVersion != null ? " " + Health.KDriveVersion : "");
                Action = NoticeAction.HealthDetails; ActionText = L.T("action.details");
                return;
            }
            if (!Health.LogLevelOk) { StatusText = L.T("notice.loglevel", Health.LogLevel); return; }
            if (!KDriveRunning)
            {
                StatusText = L.T("notice.notrunning");
                Action = NoticeAction.StartKDrive; ActionText = L.T("action.startkdrive");
                return;
            }
            // En kDrive 4, les messages de l'interface remplacent le log étendu : plus besoin de le réclamer.
            if (Mode == LogMode.Debug && VariableSet && !GuiMessages) { StatusText = L.T("notice.pending"); return; }
            if (Mode == LogMode.Debug && !GuiMessages)
            {
                StatusText = L.T(ExtendedDeclined ? "notice.inactive.declined" : "notice.inactive");
                Action = NoticeAction.EnableExtended; ActionText = L.T("action.enable");
                return;
            }
            if (Error != null) { StatusText = L.T("notice.readerror", Error); return; }
            Warning = false;
            if (UpdateAvailable) { StatusText = UpdateText; Highlight = true; return; }
            StatusText = L.T("notice.source", LogName)
                         + (GuiMessages ? L.T("notice.gui") : Mode == LogMode.Extended ? L.T("notice.extended") : "")
                         + (Health.VersionTested ? "" : L.T("notice.untested", Health.KDriveVersion));
        }

        /// <summary>Message mis en avant (bleu) plutôt qu'un avertissement : mise à jour disponible.</summary>
        public bool Highlight;

        /// <summary>
        /// Nouvelle version de kDrive Progress : annoncée dans la barre d'état, sauf si un avertissement
        /// plus important l'occupe déjà.
        /// </summary>
        public void SetAppUpdate(string version, bool installing)
        {
            if (version == null || Warning) return;
            StatusText = L.T(installing ? "update.app.installing" : "update.app.available", version);
            Action = installing ? NoticeAction.None : NoticeAction.InstallUpdate;
            ActionText = L.T("action.install");
            Highlight = true;
        }

        /// <summary>Texte détaillé des contrôles du log, copiable (Ctrl+C dans la boîte de message).</summary>
        public string HealthReport()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(L.T("report.header", Health.KDriveVersion ?? L.T("report.unknownVersion"),
                Health.LogLevel ?? L.T("report.unknown"), Mode, LogName));
            sb.AppendLine();
            if (Health.Problems.Count == 0) sb.AppendLine(L.T("report.none"));
            else
            {
                sb.AppendLine(L.T("report.missing"));
                foreach (var p in Health.Problems) sb.AppendLine("• " + L.T("health." + p));
                sb.AppendLine();
                sb.AppendLine(L.T("report.explain"));
                sb.AppendLine();
                sb.AppendLine(L.T("report.diag"));
            }
            return sb.ToString();
        }

        /// <summary>État de kDrive en quelques mots, pour l'en-tête et l'infobulle.</summary>
        public string EngineText
        {
            get
            {
                if (!Ready || !LogFound) return null;
                if (!KDriveRunning) return L.T("engine.stopped");
                var now = DateTime.Now;
                string since = Engine.Since != default && (now - Engine.Since).TotalSeconds >= 60
                    ? L.T("engine.since", Format.Duration((now - Engine.Since).TotalSeconds)) : "";
                switch (Engine.State)
                {
                    case EngineState.Offline: return L.T("engine.offline") + since;
                    case EngineState.Paused: return L.T("engine.paused") + since;
                    case EngineState.Scanning: return L.T("engine.scanning") + since;
                    case EngineState.Syncing:
                        return (Engine.PassTotal > 1 ? L.T("engine.syncing.pass", Engine.PassDone, Engine.PassTotal) : L.T("engine.syncing"))
                               + (Engine.EtaSeconds > 0 ? L.T("engine.eta", Format.Duration(Engine.EtaSeconds.Value)) : "");
                    case EngineState.Stopped: return L.T("engine.syncstopped") + since;
                    case EngineState.Error: return L.T("engine.error") + since;
                    case EngineState.UpToDate: return L.T("engine.uptodate");
                    default: return null;
                }
            }
        }

        public bool EngineWarning => !KDriveRunning || Engine.State == EngineState.Offline || Engine.State == EngineState.Paused
                                     || Engine.State == EngineState.Stopped || Engine.State == EngineState.Error;

        public static ViewState Capture(Engine engine, bool withHistory, long minBytes, bool extendedDeclined, bool withErrors)
        {
            var now = DateTime.Now;
            var s = new ViewState
            {
                LogName = engine.LogName,
                LogFound = engine.LogFound,
                KDriveRunning = engine.KDriveRunning,
                Ready = engine.Ready,
                Error = engine.LastError,
            };
            lock (engine.Tracker.Sync)
            {
                s.Active = engine.Tracker.ActiveViews(now, minBytes);
                s.Hidden = engine.Tracker.ActiveCount - s.Active.Count;
                s.History = withHistory ? engine.Tracker.HistoryViews(100, minBytes) : null;
                s.Finished = engine.Tracker.DrainFinished();
                s.Engine = engine.Tracker.EngineStatus(now, engine.Tracker.ActiveCount > 0);
                s.Mode = engine.Tracker.Mode;
                s.Health = engine.Tracker.Health(s.KDriveRunning);
                s.GuiMessages = engine.Tracker.GuiMessages;
                s.QuotaUsed = engine.Tracker.QuotaUsed;
                s.QuotaTotal = engine.Tracker.QuotaTotal;
                s.UpdateState = engine.Tracker.UpdateState;
                s.KDriveErrorCount = engine.Tracker.KDriveErrorCount;
                s.KDriveErrors = withErrors ? engine.Tracker.KDriveErrorViews() : null;
            }
            s.VariableSet = ExtendedLog.VariableSet;
            s.ExtendedDeclined = extendedDeclined;
            s.ComputeNotice();
            return s;
        }
    }
}
