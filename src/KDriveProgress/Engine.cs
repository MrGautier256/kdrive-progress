using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace KDriveProgress
{
    /// <summary>
    /// Boucle de fond : suit le log courant de kDrive (et bascule sur le nouveau au redémarrage du moteur),
    /// sonde le disque pour les downloads et enregistre l'historique. Lecture seule vis-à-vis de kDrive.
    /// </summary>
    sealed class Engine : IDisposable
    {
        const long BacklogBytes = 40L << 20; // relu au lancement pour retrouver les transferts déjà en cours
        const int TickMs = 500;

        public readonly Tracker Tracker = new Tracker();
        readonly string _logDir;
        readonly HistoryStore _store;
        readonly AutoResetEvent _wake = new AutoResetEvent(false);
        Thread _thread;
        volatile bool _stop;
        LogTail _tail;
        DateTime _lastDirCheck, _lastProcCheck, _lastSave;

        public string LogName { get; private set; }
        public bool LogFound { get; private set; }
        public bool KDriveRunning { get; private set; } = true;
        public string KDriveExe { get; private set; }
        public string LastError { get; private set; }
        public bool Ready { get; private set; }

        public Engine(string logDir, HistoryStore store)
        {
            _logDir = logDir;
            _store = store;
        }

        public void Start()
        {
            var saved = _store?.Load();
            if (saved != null) lock (Tracker.Sync) Tracker.LoadHistory(saved);
            _thread = new Thread(Loop) { IsBackground = true, Name = "kDrive log", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        void Loop()
        {
            while (!_stop)
            {
                try
                {
                    Tick();
                    LastError = null;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    if (_tail != null && !File.Exists(_tail.Path)) { _tail.Dispose(); _tail = null; }
                }
                Ready = true;
                _wake.WaitOne(TickMs);
            }
            SaveHistory(force: true);
        }

        /// <summary>Un passage complet, appelable aussi en mode relecture.</summary>
        void Tick()
        {
            var now = DateTime.Now;
            if (_tail == null || now - _lastDirCheck >= TimeSpan.FromSeconds(2))
            {
                _lastDirCheck = now;
                var latest = ChooseLog();
                LogFound = latest != null;
                if (latest != null && (_tail == null || !string.Equals(latest, _tail.Path, StringComparison.OrdinalIgnoreCase)))
                    SwitchTo(latest, initial: _tail == null && LogName == null);
                else if (_tail != null && _tail.Replaced())
                {
                    // Rotation (même session kDrive) : on finit l'ancien fichier puis on lit le nouveau depuis le début.
                    var rest = _tail.ReadNew(Tracker.IsInteresting);
                    lock (Tracker.Sync) foreach (var l in rest) Tracker.ProcessLine(l);
                    var path = _tail.Path;
                    _tail.Dispose();
                    _tail = new LogTail(path);
                }
            }

            if (_tail != null)
            {
                var lines = _tail.ReadNew(Tracker.IsInteresting, 64L << 20);
                if (lines.Count > 0)
                    lock (Tracker.Sync) foreach (var l in lines) Tracker.ProcessLine(l);
            }

            lock (Tracker.Sync)
            {
                Tracker.Probe(now);
                Tracker.Housekeeping(now);
                Tracker.Live = true; // la relecture initiale est faite : la suite se passe en direct
            }

            if (now - _lastProcCheck >= TimeSpan.FromSeconds(5))
            {
                _lastProcCheck = now;
                var procs = Process.GetProcessesByName("kDrive");
                KDriveRunning = procs.Length > 0;
                foreach (var p in procs)
                {
                    // kDrive 4 a deux kDrive.exe : le moteur et, dans client\, le lanceur de l'interface. On garde le moteur.
                    if (KDriveExe == null)
                        try
                        {
                            var exe = p.MainModule?.FileName;
                            if (exe != null && exe.IndexOf(@"\client\", StringComparison.OrdinalIgnoreCase) < 0) KDriveExe = exe;
                        }
                        catch (Exception) { }
                    p.Dispose();
                }
            }

            if (now - _lastSave >= TimeSpan.FromSeconds(10)) SaveHistory(force: false);
        }

        readonly System.Collections.Generic.HashSet<string> _deadLogs =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Le log que le moteur kDrive écrit réellement : le plus récent, sauf log mort ou pas encore confirmé.</summary>
        string ChooseLog()
        {
            string young = null;
            foreach (var path in LogTail.ListLogs(_logDir))
            {
                if (_deadLogs.Contains(path)) continue;
                if (_tail != null && string.Equals(path, _tail.Path, StringComparison.OrdinalIgnoreCase)) return path; // rien de plus récent de confirmé
                switch (LogTail.Probe(path))
                {
                    case LogTail.Liveness.Alive: return path;
                    case LogTail.Liveness.Dead: _deadLogs.Add(path); break;
                    default: if (young == null) young = path; break; // à revoir au prochain passage
                }
            }
            return _tail?.Path ?? young;
        }

        void SwitchTo(string path, bool initial)
        {
            var now = DateTime.Now;
            if (_tail != null)
            {
                lock (Tracker.Sync) Tracker.OnNewLogFile(now);
                _tail.Dispose();
                _tail = null;
            }
            var tail = new LogTail(path);
            var head = tail.ReadHead(2 << 20);
            lock (Tracker.Sync) foreach (var l in head) Tracker.ProcessHeadLine(l);
            if (initial)
            {
                tail.SeekFromEnd(BacklogBytes);
                // kDrive 4 : l'état durable (erreurs, quota, mise à jour, passe) a pu être annoncé bien avant la portion relue.
                // On parcourt le reste du fichier en ne gardant que ces messages (quelques secondes pour un log de plusieurs centaines de Mo).
                var before = tail.ScanRange(2 << 20, tail.Position, l => l.IndexOf("Sending message: {", StringComparison.Ordinal) >= 0);
                lock (Tracker.Sync) foreach (var l in before) Tracker.ProcessStateLine(l);
                var lines = tail.ReadNew(Tracker.IsInteresting);
                lock (Tracker.Sync)
                {
                    foreach (var l in lines) Tracker.ProcessLine(l);
                    Tracker.DropStale(now, TimeSpan.FromMinutes(10));
                }
            }
            _tail = tail;
            LogName = tail.Name;
        }

        void SaveHistory(bool force)
        {
            if (_store == null) return;
            _lastSave = DateTime.Now;
            System.Collections.Generic.List<Transfer> items;
            lock (Tracker.Sync)
            {
                if (!Tracker.HistoryDirty && !force) return;
                Tracker.HistoryDirty = false;
                items = Tracker.HistoryItems();
            }
            try { _store.Save(items); } catch (Exception) { /* sans gravité : réessayé au prochain passage */ }
        }

        public void Dispose()
        {
            _stop = true;
            _wake.Set();
            _thread?.Join(3000);
            _tail?.Dispose();
        }

        /// <summary>
        /// Outil de diagnostic : relit un fichier de log en entier et écrit les transferts reconstitués.
        /// KDriveProgress.exe --replay &lt;log&gt; &lt;sortie.txt&gt;
        /// </summary>
        public static void Replay(string logPath, string outPath)
        {
            var tr = new Tracker();
            using (var tail = new LogTail(logPath))
            using (var w = new StreamWriter(outPath, false, new System.Text.UTF8Encoding(true)))
            {
                foreach (var l in tail.ReadHead(2 << 20)) tr.ProcessHeadLine(l);
                foreach (var l in tail.ReadNew(Tracker.IsInteresting)) tr.ProcessLine(l);
                var end = tr.LastLineTime;
                tr.Housekeeping(end.AddSeconds(1));
                w.WriteLine("Mode: " + tr.Mode + " · sync roots: " + string.Join(", ", tr.Roots));
                var st = tr.EngineStatus(end, tr.ActiveCount > 0);
                w.WriteLine("Engine state at end of log: {0} (since {1:HH:mm:ss}) · pass {2}/{3}", st.State, st.Since, st.PassDone, st.PassTotal);
                var health = tr.Health(kdriveRunning: true);
                w.WriteLine("kDrive 4 messages: {0} · quota {1} / {2} · update state {3} · kDrive errors {4}",
                    tr.GuiMessages, tr.QuotaUsed, tr.QuotaTotal, tr.UpdateState, tr.KDriveErrorCount);
                foreach (var e in tr.KDriveErrorViews()) w.WriteLine("  ! {0:HH:mm:ss} {1} | {2}", e.End, e.Status, e.Path);
                w.WriteLine("kDrive {0} · log level {1} · log check: {2}", health.KDriveVersion ?? "?", health.LogLevel ?? "?",
                    health.Problems.Count == 0 ? "OK" : string.Join(" ; ", health.Problems.Select(p => L.T("health." + p))));
                w.WriteLine();
                w.WriteLine("== In progress at end of log");
                foreach (var v in tr.ActiveViews(end))
                    w.WriteLine("{0} {1} | {2} / {3} | {4} | {5}", v.Dir == Direction.Up ? "↑" : "↓", v.Name, v.Done, v.Total, v.Kind, v.Path);
                w.WriteLine();
                w.WriteLine("== History (most recent first)");
                foreach (var v in tr.HistoryViews(Tracker.HistoryMax))
                    w.WriteLine("{0:HH:mm:ss.fff} {1} {2,-8} {3} | {4} | {5} | {6:N1} s | {7}", v.End, v.Dir == Direction.Up ? "↑" : "↓",
                        v.Kind, v.Status, v.Name, v.Total, (v.End - v.Start).TotalSeconds, v.Path);
            }
        }
    }
}
