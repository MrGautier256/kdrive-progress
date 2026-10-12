using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace KDriveProgress
{
    enum LogMode { Unknown, Extended, Debug }

    enum EngineState { Unknown, UpToDate, Scanning, Syncing, Paused, Offline }

    /// <summary>
    /// Santé de la lecture du log : après une mise à jour de kDrive, les lignes dont l'app a besoin
    /// peuvent changer ou disparaître. Ces contrôles le détectent pour prévenir l'utilisateur.
    /// </summary>
    sealed class LogHealth
    {
        public string KDriveVersion, LogLevel;
        public List<string> Problems = new List<string>();
        public bool VersionTested => KDriveVersion == null || KDriveVersion.StartsWith("3.8.", StringComparison.Ordinal);
        public bool LogLevelOk => LogLevel == null || LogLevel == "Debug";
    }

    /// <summary>Ce que fait le moteur kDrive en ce moment, déduit des étapes de synchro du log.</summary>
    sealed class EngineInfo
    {
        public EngineState State;
        public DateTime Since;          // début de l'analyse ou de la pause (default si inconnu)
        public int PassDone, PassTotal; // éléments de la passe de synchro en cours (log étendu)
    }

    /// <summary>
    /// Reconstitue les transferts en cours à partir des lignes du log du moteur kDrive (3.8.x)
    /// et de sondes en lecture seule sur le disque.
    ///
    /// Source principale (mode « Extended Full Log ») : « Send progress for syncDbId=… path=… size=… progress=…% ».
    /// Compléments : début des requêtes, chunks des upload sessions, en-têtes des downloads,
    /// taille des fichiers temporaires de .kDrive-cache et plages hydratées des placeholders.
    /// Toutes les méthodes publiques doivent être appelées sous <see cref="Sync"/>.
    /// </summary>
    sealed class Tracker
    {
        public readonly object Sync = new object();

        // ------------------------------------------------------------------ motifs du log

        const RegexOptions Opt = RegexOptions.Compiled | RegexOptions.CultureInvariant;
        static readonly Regex RxSending = new Regex(@"^Sending (GET|POST|PUT|DELETE) request (\d+) : (\S+)", Opt);
        static readonly Regex RxFinished = new Regex(@"^Request (\d+) finished with status: (\d+)", Opt);
        static readonly Regex RxAborted = new Regex(@"^Request (\d+): aborted", Opt);
        static readonly Regex RxError = new Regex(@"^Error with request (\d+) ", Opt);
        static readonly Regex RxSyncing = new Regex(@"^Setting syncing status to: (true|false) for file: path='(.*)'$", Opt);
        static readonly Regex RxFetch = new Regex(@"^updateFetchStatus: path='(.*)'$", Opt);
        static readonly Regex RxSendProgress = new Regex(@"^Send progress for syncDbId=(\d+) path=(.*) size=(-?\d+) progress=(-?\d+)% to (new )?gui$", Opt);
        static readonly Regex RxChunksInit = new Regex(@"^Chunks initialized: File size: (\d+) / chunk size: (\d+) / nb chunks: (\d+)", Opt);
        static readonly Regex RxStartSession = new Regex(@"^Starting upload session (\d+) for file (.*) with (\d+) threads$", Opt);
        static readonly Regex RxQueued = new Regex(@"^Session ([0-9a-f-]{36}), job (\d+) queued", Opt);
        static readonly Regex RxSessionEnd = new Regex(@"^Upload session job (\d+) (finished|aborted) after", Opt);
        static readonly Regex RxAbortSession = new Regex(@"^Aborting upload session: ([0-9a-f-]{36})", Opt);
        static readonly Regex RxContentLength = new Regex(@"^Content-Length: (\d+)$", Opt);
        static readonly Regex RxDisposition = new Regex(@"^Content-Disposition: .*filename\*=utf-8''(.+)$", Opt | RegexOptions.IgnoreCase);
        static readonly Regex RxFileStatus = new Regex(@"^fileStatusChanged: path='(.*)' status = (\w+)\(\d+\)$", Opt);
        static readonly Regex RxStartImpl = new Regex(@"^startImpl: syncDbId=(\d+)", Opt);
        static readonly Regex RxSyncRoot = new Regex(@"Registering sync root for folder path: (.+)$", Opt);
        static readonly Regex RxRegisterPath = new Regex(@"REGISTER_PATH:(.+)$", Opt);
        static readonly Regex RxExtended = new Regex(@"^(?:Extended log activated: (\w+)|Extended logging is (enabled|disabled)|Extended log forced)", Opt);
        static readonly Regex RxConvert = new Regex(@"^convertToPlaceholder: path='(.*)'$", Opt);
        static readonly Regex RxStep = new Regex(@"^\*\d+\* \*{5} Step <(\w+)(?: \(.*\))?> (start|has finished)$", Opt);
        static readonly Regex RxPause = new Regex(@"^\*\d+\* (?:\*{5} Pause$|Worker Main pause$)", Opt);
        static readonly Regex RxUnpause = new Regex(@"^\*\d+\* Worker Main unpause", Opt);
        static readonly Regex RxNetworkDown = new Regex(@"^\*\d+\* Worker .+ has finished with code=NetworkError", Opt);
        static readonly Regex RxOpsGenerated = new Regex(@"^\*\d+\* (\d+) operations generated$", Opt);
        static readonly Regex RxHandling = new Regex(@"^\*\d+\* Handling ExitInfo\{(\w+)-(\w+)", Opt);
        static readonly Regex RxErrorList = new Regex(@"^\*(\d+)\* Item added in error list - node ID='.*?' - side='(\w+)\(\d+\)' - path='(.*)'$", Opt);
        static readonly Regex RxVersion = new Regex(@"^version: (\S+)$", Opt);
        static readonly Regex RxLogLevel = new Regex(@"^Log level: (\w+)", Opt);
        static readonly Regex RxUrlChunk = new Regex(@"/upload/session/([0-9a-f-]{36})/chunk\?.*chunk_size=(\d+)", Opt);
        static readonly Regex RxUrlFinish = new Regex(@"/upload/session/([0-9a-f-]{36})/finish", Opt);
        static readonly Regex RxUrlDirect = new Regex(@"/upload\?(.*)$", Opt);
        static readonly Regex RxUrlDownload = new Regex(@"/files/(\d+)/download", Opt);

        static readonly string[] Tokens =
        {
            "Send progress", " request ", "Request ", "syncing status to", "updateFetchStatus", "Chunks initialized",
            "Session ", "pload session", "Content-Length", "Content-Disposition", "Update finished", "fileStatusChanged",
            "xtended log", "sync root for folder", "startImpl", "REGISTER_PATH", "convertToPlaceholder",
            "Step <", "Worker Main", "NetworkError", "operations generated", "error list", "Handling ExitInfo", "Item inserted in ",
            "version: ", "Log level", "SYNC_COMPLETEDITEM",
        };

        /// <summary>Pré-filtre rapide : l'immense majorité des lignes (vfs_win, workers…) ne nous concerne pas.</summary>
        public static bool IsInteresting(string line)
        {
            if (line.Length < 40) return false;
            foreach (var t in Tokens)
                if (line.IndexOf(t, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        // ------------------------------------------------------------------ état

        sealed class ThreadInfo
        {
            public string SyncPath;
            public DateTime SyncAt;
            public Transfer Job;         // download ou upload direct en cours sur ce thread
            public Transfer Headers;     // download dont on attend les en-têtes de réponse
            public Transfer Session;     // upload session pilotée par ce thread
            public string PendingSyncDbId;
            public string LastCause;     // dernière erreur traitée par l'exécuteur sur ce thread
        }

        sealed class Req
        {
            public Transfer Item;
            public bool Chunk, Finish;
            public long Size;
            public int Thread;
        }

        readonly List<Transfer> _active = new List<Transfer>();
        readonly List<Transfer> _history = new List<Transfer>();
        readonly HashSet<string> _historyKeys = new HashSet<string>();
        readonly Dictionary<string, Req> _requests = new Dictionary<string, Req>();
        readonly Dictionary<string, Transfer> _sessions = new Dictionary<string, Transfer>();
        readonly Dictionary<int, ThreadInfo> _threads = new Dictionary<int, ThreadInfo>();
        readonly Dictionary<string, string> _roots = new Dictionary<string, string>();
        readonly List<string> _registeredPaths = new List<string>();
        readonly List<string> _recentPaths = new List<string>();
        readonly Dictionary<string, DateTime> _syncErrors = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        long _seq;

        // Santé du log (remis à zéro à chaque nouveau fichier de log)
        string _kdriveVersion, _logLevel;
        bool _sawModeLine;
        int _headLines, _headParsed, _interesting, _unparsed, _requestLines, _completedSignals, _sendProgressLines;
        DateTime _firstLine;

        // État du moteur
        string _step;
        DateTime _analysisSince, _scanSince, _lastScan, _pausedAt;
        bool _paused, _networkDown;
        int _passTotal, _passDone;

        public const int HistoryMax = 200;

        /// <summary>Faux pendant la relecture initiale du log : on ne notifie que ce qui se termine en direct.</summary>
        public bool Live;
        /// <summary>Transferts terminés en direct, pas encore remis à l'interface (notifications).</summary>
        public readonly List<Transfer> Finished = new List<Transfer>();

        public LogMode Mode { get; private set; }
        public DateTime LastLineTime { get; private set; }
        public bool HistoryDirty;

        // ------------------------------------------------------------------ racines de synchro

        public IEnumerable<string> Roots
        {
            get
            {
                var all = _roots.Values.Concat(_registeredPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (all.Count == 0) all.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "kDrive"));
                return all;
            }
        }

        string RootFor(string syncDbId)
        {
            if (_roots.TryGetValue(syncDbId, out var r)) return r;
            return Roots.First();
        }

        // ------------------------------------------------------------------ cycle de vie

        Transfer New(Direction dir, TransferKind kind, DateTime t)
        {
            var tr = new Transfer { Id = ++_seq, Dir = dir, Kind = kind, Start = t, LastActivity = t, LastProgress = t };
            _active.Add(tr);
            return tr;
        }

        void Complete(Transfer tr, DateTime t, string status)
        {
            if (!tr.Active) return;
            _active.Remove(tr);
            tr.Status = status;
            tr.End = t < tr.Start ? tr.Start : t;
            if (status == "OK")
            {
                if (tr.Total <= 0 && tr.Path != null)
                {
                    // Sans log étendu, la taille d'un download n'est pas loggée : le fichier, lui, est arrivé.
                    try { var fi = new FileInfo(tr.Path); if (fi.Exists) tr.Total = fi.Length; } catch (Exception) { }
                }
                if (tr.Total > 0) tr.Done = tr.Total;
                else if (tr.Done > 0) tr.Total = tr.Done;
            }
            foreach (var k in _requests.Where(kv => kv.Value.Item == tr).Select(kv => kv.Key).ToList()) _requests.Remove(k);
            if (tr.Uuid != null) _sessions.Remove(tr.Uuid);
            // Éléments sans intérêt : dossiers, fichiers vides, transferts sans aucune information.
            if (status == "OK" && tr.Total <= 0 && tr.Path == null && tr.Name == null) return;
            if (!_historyKeys.Add(tr.HistoryKey)) return; // déjà dans l'historique enregistré
            _history.Insert(0, tr);
            if (_history.Count > HistoryMax) _history.RemoveRange(HistoryMax, _history.Count - HistoryMax);
            HistoryDirty = true;
            if (Live && Finished.Count < 500) Finished.Add(tr);
        }

        /// <summary>kDrive a redémarré (nouveau fichier de log) : les transferts en cours sont interrompus.</summary>
        public void OnNewLogFile(DateTime now)
        {
            foreach (var tr in _active.ToList()) Complete(tr, now, Statuses.Interrupted);
            _requests.Clear();
            _sessions.Clear();
            _threads.Clear();
            Mode = LogMode.Unknown;
            _step = null;
            _paused = _networkDown = false;
            _passTotal = _passDone = 0;
            _kdriveVersion = _logLevel = null;
            _sawModeLine = false;
            _headLines = _headParsed = _interesting = _unparsed = _requestLines = _completedSignals = _sendProgressLines = 0;
            _firstLine = default;
            _roots.Clear();
            _registeredPaths.Clear();
        }

        /// <summary>Après la relecture initiale du log : oublie les transferts qui n'ont plus bougé depuis longtemps.</summary>
        public void DropStale(DateTime now, TimeSpan age)
        {
            foreach (var tr in _active.Where(x => now - x.LastActivity > age).ToList())
            {
                _active.Remove(tr);
                foreach (var k in _requests.Where(kv => kv.Value.Item == tr).Select(kv => kv.Key).ToList()) _requests.Remove(k);
                if (tr.Uuid != null) _sessions.Remove(tr.Uuid);
            }
        }

        public void LoadHistory(IEnumerable<Transfer> items)
        {
            foreach (var tr in items)
            {
                if (!_historyKeys.Add(tr.HistoryKey)) continue;
                _history.Add(tr);
            }
            _history.Sort((a, b) => b.End.CompareTo(a.End));
            if (_history.Count > HistoryMax) _history.RemoveRange(HistoryMax, _history.Count - HistoryMax);
        }

        public List<Transfer> HistoryItems() => _history.ToList();

        public void ClearHistory()
        {
            _history.Clear();
            HistoryDirty = true;
        }

        ThreadInfo Th(int tid)
        {
            if (!_threads.TryGetValue(tid, out var th)) _threads[tid] = th = new ThreadInfo();
            return th;
        }

        void AddRecentPath(string p)
        {
            _recentPaths.Add(p);
            if (_recentPaths.Count > 20000) _recentPaths.RemoveRange(0, 5000);
        }

        Transfer ActiveByPath(string path) =>
            _active.FirstOrDefault(x => x.Path != null && string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));

        // ------------------------------------------------------------------ lecture d'une ligne

        static bool ParseLine(string l, out DateTime t, out int tid, out string msg)
        {
            t = default; tid = 0; msg = null;
            // 2026-10-11 20:54:40:783 [D] (34872) fichier.cpp:499 - message
            if (l.Length < 32 || l[4] != '-' || l[10] != ' ' || l[19] != ':' || l[24] != '[') return false;
            try
            {
                t = new DateTime(N(l, 0, 4), N(l, 5, 2), N(l, 8, 2), N(l, 11, 2), N(l, 14, 2), N(l, 17, 2), N(l, 20, 3));
            }
            catch (ArgumentException) { return false; }
            int p = l.IndexOf('(', 26);
            int q = p < 0 ? -1 : l.IndexOf(')', p);
            if (q < 0) return false;
            if (!int.TryParse(l.Substring(p + 1, q - p - 1), out tid)) return false;
            int m = l.IndexOf(" - ", q, StringComparison.Ordinal);
            if (m < 0) return false;
            msg = l.Substring(m + 3);
            return true;
        }

        static int N(string s, int start, int len)
        {
            int v = 0;
            for (int i = start; i < start + len; i++)
            {
                int d = s[i] - '0';
                if (d < 0 || d > 9) throw new ArgumentException();
                v = v * 10 + d;
            }
            return v;
        }

        public void ProcessLine(string line)
        {
            _interesting++;
            if (!ParseLine(line, out var t, out var tid, out var msg)) { _unparsed++; return; }
            LastLineTime = t;
            if (_firstLine == default) _firstLine = t;
            Match m;

            if (msg.StartsWith("Send gui signal", StringComparison.Ordinal))
            {
                if (msg.IndexOf("SYNC_COMPLETEDITEM", StringComparison.Ordinal) >= 0) _completedSignals++;
                return;
            }

            // Analyse du drive (des centaines de milliers de lignes au démarrage) : on ne garde que l'heure.
            if (msg.IndexOf("Item inserted in ", StringComparison.Ordinal) >= 0)
            {
                if ((t - _lastScan).TotalSeconds > 15) _scanSince = t;
                _lastScan = t;
                return;
            }
            if (msg.StartsWith("*", StringComparison.Ordinal) && ProcessEngineLine(t, tid, msg)) return;

            if (msg.StartsWith("Send progress", StringComparison.Ordinal))
            {
                if ((m = RxSendProgress.Match(msg)).Success) { _sendProgressLines++; OnSendProgress(t, tid, m); }
                return;
            }
            if ((m = RxSending.Match(msg)).Success) { _requestLines++; OnSending(t, tid, m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value); return; }
            if ((m = RxFinished.Match(msg)).Success) { OnFinished(t, tid, m.Groups[1].Value, int.Parse(m.Groups[2].Value)); return; }
            if ((m = RxContentLength.Match(msg)).Success)
            {
                var th = Th(tid);
                if (th.Headers != null)
                {
                    if (th.Headers.Active) { th.Headers.Total = long.Parse(m.Groups[1].Value); th.Headers.TotalExact = true; }
                    th.Headers = null;
                }
                return;
            }
            if ((m = RxDisposition.Match(msg)).Success)
            {
                var tr = Th(tid).Headers;
                if (tr != null && tr.Path == null && string.IsNullOrEmpty(tr.Name))
                {
                    try { tr.Name = Uri.UnescapeDataString(m.Groups[1].Value.Trim().Trim('"')); } catch (UriFormatException) { }
                }
                return;
            }
            if ((m = RxSyncing.Match(msg)).Success) { OnSyncing(t, tid, m.Groups[1].Value == "true", m.Groups[2].Value); return; }
            if ((m = RxFetch.Match(msg)).Success) { OnFetch(t, tid, m.Groups[1].Value); return; }
            if (msg.EndsWith("Update finished!", StringComparison.Ordinal))
            {
                var job = Th(tid).Job;
                if (job != null && job.Active && job.Dir == Direction.Down && job.Total > 0) job.SetDone(job.Total, t);
                return;
            }
            if ((m = RxChunksInit.Match(msg)).Success)
            {
                var th = Th(tid);
                var s = th.Session;
                if (s == null || !s.Active || s.Total > 0) th.Session = s = New(Direction.Up, TransferKind.Session, t);
                s.Total = long.Parse(m.Groups[1].Value);
                s.TotalExact = true;
                s.NbChunks = int.Parse(m.Groups[3].Value);
                s.State = "starting";
                s.Touch(t);
                return;
            }
            if ((m = RxStartSession.Match(msg)).Success)
            {
                var s = New(Direction.Up, TransferKind.Session, t);
                s.Name = m.Groups[2].Value;
                s.State = "starting";
                Th(tid).Session = s;
                return;
            }
            if ((m = RxQueued.Match(msg)).Success)
            {
                var uuid = m.Groups[1].Value;
                var s = Th(tid).Session;
                if (!_sessions.ContainsKey(uuid) && s != null && s.Active && s.Uuid == null) { s.Uuid = uuid; _sessions[uuid] = s; }
                return;
            }
            if ((m = RxSessionEnd.Match(msg)).Success)
            {
                var th = Th(tid);
                if (th.Session != null)
                {
                    Complete(th.Session, t, m.Groups[2].Value == "finished" ? Statuses.Ok : Statuses.Cancelled);
                    th.Session = null;
                }
                return;
            }
            if ((m = RxAbortSession.Match(msg)).Success)
            {
                if (_sessions.TryGetValue(m.Groups[1].Value, out var s)) Complete(s, t, Statuses.Cancelled);
                return;
            }
            if ((m = RxAborted.Match(msg)).Success) { OnRequestFailed(t, m.Groups[1].Value, Statuses.Cancelled); return; }
            if ((m = RxError.Match(msg)).Success)
            {
                // « retrying… » : kDrive relance la même requête, ce n'est pas encore un échec.
                if (msg.IndexOf("retrying", StringComparison.Ordinal) < 0) OnRequestFailed(t, m.Groups[1].Value, Statuses.Network);
                return;
            }
            if ((m = RxFileStatus.Match(msg)).Success) { OnFileStatus(t, m.Groups[1].Value, m.Groups[2].Value); return; }
            if ((m = RxConvert.Match(msg)).Success) { AddRecentPath(m.Groups[1].Value); return; }
            if (ProcessConfigLine(tid, msg)) return;
        }

        // ------------------------------------------------------------------ état du moteur et erreurs de synchro

        /// <summary>Lignes « *1* … » du moteur de synchro : étapes, pause, réseau, erreurs par fichier.</summary>
        bool ProcessEngineLine(DateTime t, int tid, string msg)
        {
            Match m;
            if ((m = RxStep.Match(msg)).Success)
            {
                if (m.Groups[2].Value != "start") return true;
                var step = m.Groups[1].Value;
                if (step == "Done") return true; // transitoire, suivi de Idle
                if (step == "UpdateDetection1" || (_step == "Idle" && step != "Idle")) _analysisSince = t;
                if (step == "Idle") { _passTotal = 0; _passDone = 0; }
                _step = step;
                _paused = false;       // une étape qui démarre : le moteur tourne
                _networkDown = false;
                return true;
            }
            if (RxPause.IsMatch(msg))
            {
                if (!_paused) _pausedAt = t;
                _paused = true;
                return true;
            }
            if (RxUnpause.IsMatch(msg)) { _paused = false; _networkDown = false; return true; }
            if (RxNetworkDown.IsMatch(msg))
            {
                if (!_networkDown) _pausedAt = t;
                _networkDown = true;
                return true;
            }
            if ((m = RxOpsGenerated.Match(msg)).Success)
            {
                _passTotal = int.Parse(m.Groups[1].Value);
                _passDone = 0;
                return true;
            }
            if ((m = RxHandling.Match(msg)).Success)
            {
                Th(tid).LastCause = m.Groups[2].Value == "Unknown" ? m.Groups[1].Value : m.Groups[2].Value;
                return true;
            }
            if ((m = RxErrorList.Match(msg)).Success)
            {
                OnSyncError(t, tid, m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
                return true;
            }
            return false;
        }

        /// <summary>kDrive met un élément de côté après un échec : on l'ajoute à l'historique comme erreur.</summary>
        void OnSyncError(DateTime t, int tid, string syncDbId, string side, string rel)
        {
            var path = System.IO.Path.Combine(RootFor(syncDbId), rel);
            var th = Th(tid);
            var cause = th.LastCause;
            th.LastCause = null;
            // kDrive réessaie à chaque passe : une seule entrée par fichier toutes les 6 heures.
            if (_syncErrors.TryGetValue(path, out var when) && (t - when).TotalHours < 6) return;
            _syncErrors[path] = t;
            var tr = ActiveByPath(path) ?? New(side == "Remote" ? Direction.Down : Direction.Up, TransferKind.Other, t);
            tr.Path = path;
            Complete(tr, t, Statuses.SyncError + (cause != null ? ":" + cause : ""));
        }

        public EngineInfo EngineStatus(DateTime now, bool hasActive)
        {
            var info = new EngineInfo { PassDone = _passDone, PassTotal = _passTotal };
            if (_networkDown) { info.State = EngineState.Offline; info.Since = _pausedAt; }
            else if (_paused) { info.State = EngineState.Paused; info.Since = _pausedAt; }
            else if (hasActive || _step == "Propagation2") info.State = EngineState.Syncing;
            else if (_step != null && _step != "Idle") { info.State = EngineState.Scanning; info.Since = _analysisSince; }
            else if ((now - _lastScan).TotalSeconds < 15) { info.State = EngineState.Scanning; info.Since = _scanSince; }
            else info.State = _step == null ? EngineState.Unknown : EngineState.UpToDate;
            return info;
        }

        /// <summary>Lignes de démarrage : racines de synchro et mode du log.</summary>
        bool ProcessConfigLine(int tid, string msg)
        {
            Match m;
            if ((m = RxExtended.Match(msg)).Success)
            {
                var v = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : "true";
                Mode = v == "true" || v == "1" || v == "enabled" ? LogMode.Extended : LogMode.Debug;
                _sawModeLine = true;
                return true;
            }
            if ((m = RxVersion.Match(msg)).Success) { _kdriveVersion = m.Groups[1].Value; return true; }
            if ((m = RxLogLevel.Match(msg)).Success) { _logLevel = m.Groups[1].Value; return true; }
            if ((m = RxStartImpl.Match(msg)).Success) { Th(tid).PendingSyncDbId = m.Groups[1].Value; return true; }
            if ((m = RxSyncRoot.Match(msg)).Success)
            {
                var id = Th(tid).PendingSyncDbId;
                if (id != null) _roots[id] = m.Groups[1].Value.Trim().TrimEnd('\\');
                return true;
            }
            if ((m = RxRegisterPath.Match(msg)).Success)
            {
                var p = m.Groups[1].Value.Trim().Replace('/', '\\').TrimEnd('\\');
                if (!_registeredPaths.Contains(p, StringComparer.OrdinalIgnoreCase)) _registeredPaths.Add(p);
                return true;
            }
            return false;
        }

        /// <summary>Lignes du début du fichier, lues à part quand on démarre en cours de route.</summary>
        public void ProcessHeadLine(string line)
        {
            line = line.TrimEnd('\r');
            if (line.Length == 0) return;
            _headLines++;
            if (!ParseLine(line, out _, out var tid, out var msg)) return;
            _headParsed++;
            ProcessConfigLine(tid, msg);
        }

        /// <summary>
        /// Contrôles de cohérence : chaque problème signifie qu'une information attendue n'est plus trouvée,
        /// probablement parce qu'une mise à jour de kDrive a changé son log.
        /// </summary>
        public LogHealth Health(bool kdriveRunning)
        {
            var h = new LogHealth { KDriveVersion = _kdriveVersion, LogLevel = _logLevel };
            bool headKnown = _headLines >= 20;
            if ((headKnown && _headParsed * 2 < _headLines) || (_interesting >= 50 && _unparsed * 2 > _interesting))
                h.Problems.Add("format");
            if (headKnown && !_sawModeLine)
                h.Problems.Add("mode");
            if (headKnown && _roots.Count == 0 && _registeredPaths.Count == 0)
                h.Problems.Add("root");
            if (kdriveRunning && h.LogLevelOk && (LastLineTime - _firstLine).TotalMinutes >= 5 && _requestLines == 0)
                h.Problems.Add("requests");
            if (Mode == LogMode.Extended && _completedSignals >= 5 && _sendProgressLines == 0)
                h.Problems.Add("sendprogress");
            return h;
        }

        // ------------------------------------------------------------------ requêtes HTTP

        void OnSending(DateTime t, int tid, string verb, string rid, string url)
        {
            var th = Th(tid);
            th.Headers = null;
            // Un thread n'exécute qu'un job à la fois : une nouvelle requête signifie que le précédent est fini.
            if (th.Job != null && th.Job.RequestId != rid) CloseThreadJob(th, t);

            if (url.IndexOf("/upload/session/", StringComparison.Ordinal) >= 0)
            {
                Match m;
                if ((m = RxUrlChunk.Match(url)).Success)
                {
                    var s = GetSession(m.Groups[1].Value, t);
                    _requests[rid] = new Req { Item = s, Chunk = true, Size = long.Parse(m.Groups[2].Value), Thread = tid };
                    s.InFlight++;
                    s.State = null;
                    s.Touch(t);
                }
                else if ((m = RxUrlFinish.Match(url)).Success)
                {
                    if (_sessions.TryGetValue(m.Groups[1].Value, out var s))
                    {
                        s.State = "finishing";
                        s.Touch(t);
                        _requests[rid] = new Req { Item = s, Finish = true, Thread = tid };
                    }
                }
                return;
            }

            if (verb == "POST")
            {
                var m = RxUrlDirect.Match(url);
                if (!m.Success) return;
                var query = m.Groups[1].Value;
                var tr = New(Direction.Up, TransferKind.Direct, t);
                var total = QueryValue(query, "total_size");
                if (total != null && long.TryParse(total, out var size)) { tr.Total = size; tr.TotalExact = true; }
                tr.FileId = QueryValue(query, "file_id");
                var name = QueryValue(query, "file_name");
                if (name != null) tr.Name = name;
                if (th.SyncPath != null && (t - th.SyncAt).TotalSeconds < 3) tr.Path = th.SyncPath;
                th.SyncPath = null;
                tr.Thread = tid;
                tr.RequestId = rid;
                th.Job = tr;
                _requests[rid] = new Req { Item = tr, Thread = tid };
                return;
            }

            if (verb == "GET")
            {
                if (_requests.TryGetValue(rid, out var existing) && existing.Item.Active) return; // redirection 302
                var m = RxUrlDownload.Match(url);
                if (!m.Success) return;
                var tr = New(Direction.Down, TransferKind.Download, t);
                tr.FileId = m.Groups[1].Value;
                if (th.SyncPath != null && (t - th.SyncAt).TotalSeconds < 5) tr.Path = th.SyncPath;
                th.SyncPath = null;
                tr.Thread = tid;
                tr.RequestId = rid;
                th.Job = tr;
                _requests[rid] = new Req { Item = tr, Thread = tid };
            }
        }

        static string QueryValue(string query, string key)
        {
            foreach (var part in query.Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq > 0 && part.Substring(0, eq) == key)
                {
                    try { return Uri.UnescapeDataString(part.Substring(eq + 1).Replace('+', ' ')); }
                    catch (UriFormatException) { return part.Substring(eq + 1); }
                }
            }
            return null;
        }

        void CloseThreadJob(ThreadInfo th, DateTime t)
        {
            var job = th.Job;
            th.Job = null;
            if (job == null || !job.Active) return;
            if (job.Dir == Direction.Down && job.HeadersAt != default) Complete(job, t, Statuses.Ok);
        }

        Transfer GetSession(string uuid, DateTime t)
        {
            if (_sessions.TryGetValue(uuid, out var s)) return s;
            // Session commencée avant la portion de log relue : taille inconnue pour l'instant.
            s = New(Direction.Up, TransferKind.Session, t);
            s.Uuid = uuid;
            _sessions[uuid] = s;
            return s;
        }

        void OnFinished(DateTime t, int tid, string rid, int code)
        {
            if (!_requests.TryGetValue(rid, out var req)) return;
            if (code >= 300 && code < 400) return; // redirection vers le serveur de download
            bool ok = code >= 200 && code < 300;
            var tr = req.Item;
            if (req.Chunk)
            {
                _requests.Remove(rid);
                tr.InFlight = Math.Max(0, tr.InFlight - 1);
                if (ok)
                {
                    tr.Chunks++;
                    tr.ChunkBytes += req.Size;
                    tr.SetDone(tr.ChunkBytes, t);
                }
                return;
            }
            if (req.Finish)
            {
                _requests.Remove(rid);
                if (!ok) Complete(tr, t, Statuses.Http + ":" + code);
                return;
            }
            if (tr.Kind == TransferKind.Download)
            {
                if (ok)
                {
                    // 200 = en-têtes reçus : le contenu arrive maintenant dans .kDrive-cache.
                    tr.HeadersAt = t;
                    tr.Touch(t);
                    Th(tid).Headers = tr;
                }
                else Complete(tr, t, Statuses.Http + ":" + code);
                return;
            }
            _requests.Remove(rid);
            var th = Th(tid);
            if (th.Job == tr) th.Job = null;
            Complete(tr, t, ok ? Statuses.Ok : Statuses.Http + ":" + code);
        }

        void OnRequestFailed(DateTime t, string rid, string status)
        {
            if (!_requests.TryGetValue(rid, out var req)) return;
            _requests.Remove(rid);
            if (req.Chunk) { req.Item.InFlight = Math.Max(0, req.Item.InFlight - 1); return; }
            Complete(req.Item, t, status);
        }

        // ------------------------------------------------------------------ VFS

        void OnSyncing(DateTime t, int tid, bool syncing, string path)
        {
            if (syncing)
            {
                var th = Th(tid);
                th.SyncPath = path;
                th.SyncAt = t;
                AddRecentPath(path);
                return;
            }
            var tr = ActiveByPath(path);
            if (tr != null && tr.Dir == Direction.Down && tr.HeadersAt != default) Complete(tr, t, Statuses.Ok);
        }

        void OnFetch(DateTime t, int tid, string path)
        {
            var tr = ActiveByPath(path);
            if (tr == null)
            {
                var job = Th(tid).Job;
                if (job != null && job.Active && job.Dir == Direction.Down && job.Path == null) (tr = job).Path = path;
            }
            tr?.Touch(t);
        }

        void OnFileStatus(DateTime t, string path, string status)
        {
            if (status == "Success")
            {
                var tr = ActiveByPath(path);
                if (tr != null && (tr.Dir == Direction.Up || tr.HeadersAt != default)) { Complete(tr, t, Statuses.Ok); return; }
                long size = -1;
                try { var fi = new FileInfo(path); if (fi.Exists) size = fi.Length; } catch (Exception) { }
                if (size > 0) PatchHistory(path, size, t);
            }
            else if (status == "Error")
            {
                var tr = ActiveByPath(path);
                if (tr != null) Complete(tr, t, Statuses.Error);
            }
        }

        /// <summary>Upload terminé sans nom connu (modification : seul file_id apparaît) : on lui rend son chemin.</summary>
        void PatchHistory(string path, long size, DateTime t)
        {
            foreach (var h in _history)
            {
                if ((t - h.End).TotalSeconds > 120) break;
                if (h.Path == null && h.Dir == Direction.Up && h.Total == size)
                {
                    h.Path = path;
                    HistoryDirty = true;
                    return;
                }
            }
        }

        // ------------------------------------------------------------------ « Send progress » (mode étendu)

        void OnSendProgress(DateTime t, int tid, Match m)
        {
            var rel = m.Groups[2].Value;
            long size = long.Parse(m.Groups[3].Value);
            int pct = int.Parse(m.Groups[4].Value);
            var path = System.IO.Path.Combine(RootFor(m.Groups[1].Value), rel);
            var name = System.IO.Path.GetFileName(path);
            // Avancement de la passe : chaque élément terminé (une seule fois, sur la ligne « to new gui »)
            if (pct >= 100 && m.Groups[5].Success && _passTotal > 0 && _step == "Propagation2")
                _passDone = Math.Min(_passTotal, _passDone + 1);

            var tr = ActiveByPath(path);
            if (tr == null)
            {
                // Job de ce thread sans chemin connu (upload direct, download)
                var job = Th(tid).Job;
                if (job != null && job.Active && job.Path == null && (job.Total <= 0 || job.Total == size)) tr = job;
            }
            if (tr == null)
            {
                // Upload session : appariement par taille (et nom quand le mode étendu l'a donné)
                tr = _active.FirstOrDefault(x => x.Kind == TransferKind.Session && x.Path == null && x.Total == size
                                                 && (string.IsNullOrEmpty(x.Name) || x.Name == name))
                     ?? _active.FirstOrDefault(x => x.Kind == TransferKind.Session && x.Path == null && x.Total <= 0
                                                    && x.Name == name);
            }
            if (tr == null)
            {
                // Upload direct déjà en cours sur un autre thread (même taille, même nom ou nom inconnu)
                tr = _active.FirstOrDefault(x => x.Kind == TransferKind.Direct && x.Path == null && x.Total == size
                                                 && (string.IsNullOrEmpty(x.Name) || x.Name == name));
            }
            if (tr == null)
            {
                if (pct >= 100 || pct < 0)
                {
                    // Élément déjà terminé (petit fichier, dossier, déplacement…) : on complète l'historique si possible.
                    if (size > 0) PatchHistory(path, size, t);
                    return;
                }
                if (size <= 0 || Directory.Exists(path)) return;
                tr = New(GuessDirection(path, size), TransferKind.Other, t);
            }

            tr.Path = path;
            if (size > 0 && !tr.TotalExact) { tr.Total = size; tr.TotalExact = true; }
            if (pct >= 0 && size > 0) tr.SetDone((long)(size * (pct / 100.0)), t);
            else tr.Touch(t);
            if (pct >= 100) Complete(tr, t, Statuses.Ok);
        }

        static Direction GuessDirection(string path, long size)
        {
            // Placeholder pas encore entièrement présent sur le disque : c'est forcément un download.
            var attr = Native.Attributes(path);
            if (attr.HasValue && (attr.Value & Native.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS) != 0) return Direction.Down;
            return Direction.Up;
        }

        // ------------------------------------------------------------------ sondes disque

        /// <summary>Met à jour les downloads à partir du disque : fichier temporaire et plages hydratées du placeholder.</summary>
        public void Probe(DateTime now)
        {
            var downs = _active.Where(x => x.Dir == Direction.Down).OrderBy(x => x.HeadersAt).ToList();
            if (downs.Count == 0) return;

            var claimed = new HashSet<string>(downs.Where(d => d.Tmp != null).Select(d => d.Tmp), StringComparer.OrdinalIgnoreCase);
            List<KeyValuePair<string, DateTime>> tmps = null;

            foreach (var d in downs)
            {
                long best = -1;
                if (d.Tmp == null && d.HeadersAt != default)
                {
                    if (tmps == null) tmps = ListTempFiles();
                    // kDrive crée le temporaire juste après la réception des en-têtes (200).
                    var candidate = tmps
                        .Where(f => !claimed.Contains(f.Key)
                                    && f.Value >= d.HeadersAt.AddSeconds(-2) && f.Value <= d.HeadersAt.AddSeconds(15))
                        .OrderBy(f => Math.Abs((f.Value - d.HeadersAt).TotalMilliseconds))
                        .Select(f => f.Key).FirstOrDefault();
                    if (candidate != null) { d.Tmp = candidate; claimed.Add(candidate); }
                }
                if (d.Tmp != null)
                {
                    long s = Native.LiveSize(d.Tmp);
                    if (s >= 0) best = s;
                }
                if (d.Path != null)
                {
                    var attr = Native.Attributes(d.Path);
                    if (attr.HasValue && (attr.Value & Native.FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS) != 0)
                    {
                        if (d.Total <= 0)
                        {
                            // Taille logique d'un placeholder = taille du fichier distant ; la lire ne l'hydrate pas.
                            try { d.Total = new FileInfo(d.Path).Length; } catch (Exception) { }
                        }
                        best = Math.Max(best, Native.OnDiskBytes(d.Path));
                    }
                }
                if (best > d.Done) d.SetDone(best, now);
            }
        }

        List<KeyValuePair<string, DateTime>> ListTempFiles()
        {
            var list = new List<KeyValuePair<string, DateTime>>();
            foreach (var root in Roots)
            {
                try
                {
                    var dir = new DirectoryInfo(System.IO.Path.Combine(root, ".kDrive-cache"));
                    if (!dir.Exists) continue;
                    foreach (var f in dir.EnumerateFiles("kdrive_*"))
                        list.Add(new KeyValuePair<string, DateTime>(f.FullName, f.CreationTime));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return list;
        }

        /// <summary>Nettoyage périodique : fins silencieuses, transferts perdus de vue, noms par taille.</summary>
        public void Housekeeping(DateTime now)
        {
            foreach (var tr in _active.ToList())
            {
                double idle = (now - tr.LastActivity).TotalSeconds;
                if (tr.Kind == TransferKind.Download)
                {
                    // Petit download terminé sans ligne de fin (le thread est resté inactif ensuite).
                    if (tr.HeadersAt != default && idle > 20) Complete(tr, tr.LastActivity, Statuses.Ok);
                    else if (tr.HeadersAt == default && idle > 300) Complete(tr, now, Statuses.Lost);
                }
                else if (idle > 1800) Complete(tr, now, Statuses.Lost);
            }

            // Mode sans log étendu : la session d'upload ne donne pas son fichier, on le cherche par sa taille.
            foreach (var s in _active.Where(x => x.Kind == TransferKind.Session && x.Path == null && x.Total > 0 && !x.NameLookupDone))
            {
                s.NameLookupDone = true;
                if (Mode == LogMode.Extended && !string.IsNullOrEmpty(s.Name)) continue;
                s.Path = FindPathBySize(s.Total, s.Name);
            }
        }

        string FindPathBySize(long size, string name)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int checkedCount = 0;
            for (int i = _recentPaths.Count - 1; i >= 0 && checkedCount < 3000; i--)
            {
                var p = _recentPaths[i];
                if (!seen.Add(p)) continue;
                if (!string.IsNullOrEmpty(name) && !string.Equals(System.IO.Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase)) continue;
                checkedCount++;
                try
                {
                    var fi = new FileInfo(p);
                    if (fi.Exists && fi.Length == size) return p;
                }
                catch (Exception) { }
            }
            return null;
        }

        // ------------------------------------------------------------------ instantané pour l'interface

        /// <summary>Transferts en cours ; ceux dont la taille connue est sous <paramref name="minBytes"/> sont masqués.</summary>
        public List<TransferView> ActiveViews(DateTime now, long minBytes = 0) =>
            _active.Where(x => x.Total != 0 && (x.Total < 0 || x.Total >= minBytes)).OrderBy(x => x.Start).Select(x => View(x, now)).ToList();

        public int ActiveCount => _active.Count(x => x.Total != 0);

        /// <summary>Historique ; les petits transferts réussis sont masqués, jamais les échecs.</summary>
        public List<TransferView> HistoryViews(int max, long minBytes = 0) =>
            _history.Where(x => x.Status != "OK" || x.Total < 0 || x.Total >= minBytes).Take(max).Select(x => View(x, x.End)).ToList();

        /// <summary>Transferts terminés en direct depuis le dernier appel (pour les notifications).</summary>
        public List<TransferView> DrainFinished()
        {
            var list = Finished.Select(x => View(x, x.End)).ToList();
            Finished.Clear();
            return list;
        }

        static TransferView View(Transfer x, DateTime now)
        {
            var v = new TransferView
            {
                Id = x.Id, Dir = x.Dir, Kind = x.Kind, Name = x.DisplayName, Path = x.Path, Status = x.Status, State = x.State,
                Done = x.Done, Total = x.Total, Start = x.Start, End = x.End,
            };
            if (x.Total > 0) v.Fraction = Math.Min(1.0, (double)x.Done / x.Total);
            if (x.Active)
            {
                v.Rate = x.Rate(now);
                if (v.Rate > 0 && x.Total > x.Done) v.EtaSeconds = (x.Total - x.Done) / v.Rate.Value;
                if (x.Kind == TransferKind.Session && x.NbChunks > 0)
                    v.Detail = L.T("detail.chunk", x.Chunks, x.NbChunks);
                double idle = (now - x.LastProgress).TotalSeconds;
                if (idle > 60) v.StalledSeconds = idle;
            }
            else
            {
                double dur = (x.End - x.Start).TotalSeconds;
                if (dur > 0.5 && x.Total > 0 && x.Status == "OK") v.Rate = x.Total / dur;
            }
            return v;
        }
    }
}
