using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace KDriveProgress
{
    /// <summary>Erreur de synchro telle que kDrive la présente dans son interface (kDrive 4).</summary>
    sealed class KDriveError
    {
        public long DbId;
        public string Path, Status;
        public DateTime Time;
    }

    /// <summary>
    /// kDrive 4 : le moteur écrit dans son log (niveau INFO) chaque message JSON envoyé à l'interface officielle :
    /// « guicommserver.cpp - Sending message: { "id": …, "num": …, "params": { … }, "type": … } to: … ».
    /// On y lit, sans jamais se connecter à kDrive, la progression par fichier (num 17), la passe en cours (16),
    /// le quota (9), l'état de mise à jour (23, 70), les erreurs officielles (27, 28, 29, 34),
    /// les dossiers synchronisés (11). Valeurs des énumérations : kDrive w-4.0.0.16, src/libcommon/utility/cstypes.h.
    /// </summary>
    sealed partial class Tracker
    {
        const string GuiMessagePrefix = "Sending message: {";

        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 1 << 22 };

        static readonly string[] ExitCauses =
        {
            "Unknown", "WorkerExited", "DbAccessError", "DbEntryNotFound", "InvalidSnapshot", "SyncDirDoesntExist",
            "SyncDirAccessError", "SyncDirDiskMissing", "SyncDirNestingError", "SyncDirChanged", "HttpErr", "HttpErrForbidden",
            "RedirectionError", "ApiErr", "InvalidSize", "FileExists", "DirExists", "FileAccessError", "FileLocked",
            "NotEnoughDiskSpace", "DriveAccessError", "LoginError", "DriveMaintenance", "DriveNotRenew", "MigrationError",
            "MigrationProxyNotImplemented", "InconsistentPinState", "FileSizeMismatch", "UploadNotTerminated", "UnableToStartVfs",
            "NotEnoughMemory", "FileTooBig", "MoveToTrashFailed", "InvalidName", "LiteSyncNotAllowed", "LiteSyncExtNotRunning",
            "NotPlaceHolder", "NetworkTimeout", "SocketsDefuncted", "NotFound", "QuotaExceeded", "FullListParsingError",
            "OperationCanceled", "ShareLinkAlreadyExists", "InvalidArgument", "InvalidDestination", "DriveAsleep", "DriveWakingUp",
            "Http5xx", "NotEnoughINotifyWatches", "FileOrDirectoryCorrupted", "TmpDirAccessError", "UpdateTreeIntegrityCheckFailed",
            "MissingReplyData", "BlackListPropagationError", "FileSystemNotSupported", "SyncDeletionFailed", "InvalidLinkTarget",
            "MoveThroughSymlink",
        };

        static readonly string[] ExitCodes =
        {
            "Ok", "Unknown", "NetworkError", "InvalidToken", "DataError", "DbError", "BackError", "SystemError", "FatalError",
            "LogicError", "TokenRefreshed", "RateLimited", "InvalidSync", "InvalidOperation", "OperationCanceled",
            "UpdateRequired", "LogUploadFailed", "UpdateFailed",
        };

        static readonly string[] ConflictTypes =
        {
            "None", "EditDelete", "MoveDelete", "MoveParentDelete", "CreateParentDelete", "MoveMoveSource", "MoveMoveDest",
            "MoveCreate", "CreateCreate", "EditEdit", "MoveMoveCycle",
        };

        // SyncStatus : 0 Undefined, 1 Starting, 2 Running, 3 Idle, 4 PauseAsked, 5 Paused, 6 StopAsked, 7 Stopped, 8 Error
        // SyncStep   : 0 None, 1 Idle, 2-3 UpdateDetection, 4-7 Reconciliation, 8 Propagation1 (tri), 9 Propagation2 (exécution), 10 Done
        // UpdateState: 0 UpToDate, 1 Checking, 2 Available, 3 ManualUpdateAvailable, 4 Downloading, 5 Ready, 6-8 erreurs, 9 NoUpdate
        // SyncFileStatus : 1 Error, 2 Success, 3 Conflict, 4 Inconsistency, 5 Ignored, 6 Syncing
        // SyncFileInstruction : 1 Update, 2 UpdateMetadata, 3 Remove, 4 Move, 5 Get, 6 Put

        bool _guiMessages;
        int _guiSyncStatus = -1, _guiSyncStep = -1;
        bool _guiBusy;
        DateTime _guiStatusSince, _guiBusySince;
        long _passFilesDone, _passFiles, _passBytesDone, _passBytes, _passEtaMs;
        long _quotaUsed = -1, _quotaTotal = -1;
        string _driveName;
        int _updateState = -1;
        readonly Dictionary<long, KDriveError> _kdErrors = new Dictionary<long, KDriveError>();

        /// <summary>Vrai dès qu'un message de l'interface kDrive 4 a été lu dans ce log.</summary>
        public bool GuiMessages => _guiMessages;
        public long QuotaUsed => _quotaUsed;
        public long QuotaTotal => _quotaTotal;
        public string DriveName => _driveName;
        public int UpdateState => _updateState;
        public int KDriveErrorCount => _kdErrors.Count;

        void ResetGui()
        {
            _guiMessages = false;
            _guiSyncStatus = _guiSyncStep = -1;
            _guiBusy = false;
            _passFilesDone = _passFiles = _passBytesDone = _passBytes = _passEtaMs = 0;
            _updateState = -1;
            _kdErrors.Clear();
            // Le quota reste valable d'une session de kDrive à l'autre.
        }

        /// <summary>Ligne lue avant la portion relue au lancement : on n'en garde que l'état (pas la progression d'un fichier).</summary>
        public void ProcessStateLine(string line)
        {
            if (ParseLine(line.TrimEnd('\r'), out var t, out _, out var msg) && msg.StartsWith(GuiMessagePrefix, StringComparison.Ordinal))
                ProcessGuiMessage(t, msg, stateOnly: true);
        }

        void ProcessGuiMessage(DateTime t, string msg, bool stateOnly)
        {
            int end = msg.LastIndexOf(" to: ", StringComparison.Ordinal);
            if (end < 0) return;
            Dictionary<string, object> root;
            try { root = Json.DeserializeObject(msg.Substring(GuiMessagePrefix.Length - 1, end - GuiMessagePrefix.Length + 1)) as Dictionary<string, object>; }
            catch (Exception) { return; } // message tronqué par kDrive (2048 caractères) : seuls les gros messages sont concernés
            if (root == null) return;
            _guiMessages = true;
            var p = Obj(root, "params");
            if (p == null) return;
            switch (Num(root, "num"))
            {
                case 17: if (!stateOnly) OnGuiItem(t, p); break;
                case 16: OnGuiProgress(t, p); break;
                case 9: OnDriveInfo(Obj(p, "driveInfo")); break;
                case 7:
                    if (p.TryGetValue("driveInfoList", out var list) && list is object[] drives && drives.Length > 0)
                        OnDriveInfo(drives[0] as Dictionary<string, object>);
                    break;
                case 11:
                    if (p.TryGetValue("syncInfoList", out var syncs) && syncs is object[] arr)
                        foreach (var s in arr.OfType<Dictionary<string, object>>())
                        {
                            var local = B64(s, "localPath");
                            if (!string.IsNullOrEmpty(local)) _roots[Num(s, "dbId").ToString(CultureInfo.InvariantCulture)] = local.Replace('/', '\\').TrimEnd('\\');
                        }
                    break;
                case 23:
                case 70:
                    if (p.ContainsKey("updateState")) _updateState = (int)Num(p, "updateState");
                    break;
                case 27: AddKDriveError(Obj(p, "errorInfo")); break;
                case 28: _kdErrors.Remove(Num(p, "errorDbId")); break;
                case 29: _kdErrors.Clear(); break;
                case 34:
                    if (p.TryGetValue("errorInfoList", out var errs) && errs is object[] errArr)
                    {
                        _kdErrors.Clear();
                        foreach (var e in errArr.OfType<Dictionary<string, object>>()) AddKDriveError(e);
                    }
                    break;
            }
        }

        void OnGuiProgress(DateTime t, Dictionary<string, object> p)
        {
            int status = (int)Num(p, "syncStatus"), step = (int)Num(p, "syncStep");
            if (status != _guiSyncStatus) _guiStatusSince = t;
            bool busy = status == 1 || (status == 2 && step >= 2 && step <= 8);
            if (busy && !_guiBusy) _guiBusySince = t;
            _guiBusy = busy;
            _guiSyncStatus = status;
            _guiSyncStep = step;
            var sp = Obj(p, "SyncProgress");
            if (sp != null)
            {
                _passFilesDone = Num(sp, "currentFile");
                _passFiles = Num(sp, "totalFiles");
                _passBytesDone = Num(sp, "completedSize");
                _passBytes = Num(sp, "totalSize");
                _passEtaMs = Num(sp, "estimatedRemainingTime");
            }
            if (status == 2 || status == 3) { _paused = false; if (status == 3) _networkDown = false; }
        }

        void OnDriveInfo(Dictionary<string, object> d)
        {
            if (d == null) return;
            long size = Num(d, "size"), used = Num(d, "usedSize");
            if (size > 0) _quotaTotal = size;
            if (used > 0) _quotaUsed = used;
            var name = B64(d, "name");
            if (!string.IsNullOrEmpty(name)) _driveName = name;
        }

        void AddKDriveError(Dictionary<string, object> e)
        {
            if (e == null) return;
            var rel = B64(e, "path");
            var err = new KDriveError
            {
                DbId = Num(e, "dbId"),
                Path = string.IsNullOrEmpty(rel) ? null : System.IO.Path.Combine(RootFor(Num(e, "syncDbId").ToString(CultureInfo.InvariantCulture)), rel),
                Time = Num(e, "time") > 0 ? DateTimeOffset.FromUnixTimeSeconds(Num(e, "time")).LocalDateTime : DateTime.Now,
            };
            int conflict = (int)Num(e, "conflictType");
            if (conflict > 0) err.Status = Statuses.Conflict + ":" + Name(ConflictTypes, conflict);
            else if (Num(e, "inconsistencyType") > 0) err.Status = Statuses.Inconsistency;
            else
            {
                int cause = (int)Num(e, "exitCause");
                err.Status = Statuses.SyncError + ":" + (cause > 0 ? Name(ExitCauses, cause) : Name(ExitCodes, (int)Num(e, "exitCode")));
            }
            _kdErrors[err.DbId] = err;
        }

        /// <summary>Progression d'un fichier, telle que l'interface officielle la reçoit.</summary>
        void OnGuiItem(DateTime t, Dictionary<string, object> p)
        {
            var it = Obj(p, "itemInfo");
            if (it == null || Num(it, "type") != 1) return; // dossiers : rien à suivre
            int instruction = (int)Num(it, "instruction");
            if (instruction != 1 && instruction != 5 && instruction != 6) return; // seulement les transferts (Update, Get, Put)
            var rel = B64(it, "path");
            if (string.IsNullOrEmpty(rel)) return;
            var dir = Num(it, "direction") == 2 ? Direction.Down : Direction.Up;
            int status = (int)Num(it, "status");
            long size = Num(it, "size");
            int pct = (int)Num(it, "progress");
            var path = System.IO.Path.Combine(RootFor(Num(p, "syncDbId").ToString(CultureInfo.InvariantCulture)), rel);
            var name = System.IO.Path.GetFileName(path);
            var fileId = B64(it, "remoteNodeId");

            var tr = ActiveByPath(path);
            if (tr == null && dir == Direction.Down && !string.IsNullOrEmpty(fileId))
                tr = _active.FirstOrDefault(x => x.Dir == Direction.Down && x.FileId == fileId);
            if (tr == null && dir == Direction.Up)
                tr = _active.FirstOrDefault(x => (x.Kind == TransferKind.Session || x.Kind == TransferKind.Direct) && x.Path == null
                                                 && x.Total == size && (string.IsNullOrEmpty(x.Name) || x.Name == name))
                     ?? _active.FirstOrDefault(x => x.Kind == TransferKind.Session && x.Path == null && x.Total <= 0 && x.Name == name);

            if (status == 6) // en cours (jamais une fin, même à 100 %)
            {
                // kDrive 4 annonce une hydratation par un « 100 % » au démarrage : le download, lui, n'a pas commencé.
                if (dir == Direction.Down && pct >= 100) { tr?.Touch(t); return; }
                if (tr == null)
                {
                    if (size <= 0 || pct < 0) return;
                    tr = New(dir, TransferKind.Other, t);
                }
                tr.Path = path;
                if (size > 0 && !tr.TotalExact) { tr.Total = size; tr.TotalExact = true; }
                if (pct >= 0 && size > 0) tr.SetDone((long)(size * (Math.Min(pct, 99) / 100.0)), t);
                else tr.Touch(t);
                return;
            }

            if (status == 2) // réussi
            {
                if (tr != null) { tr.Path = path; Complete(tr, t, Statuses.Ok); }
                else if (size > 0) PatchHistory(path, size, t);
                return;
            }

            if (status == 1 || status == 3 || status == 4) // erreur, conflit, incohérence
            {
                string code;
                if (status == 3) code = Statuses.Conflict + ":" + Name(ConflictTypes, (int)Num(it, "conflict"));
                else if (status == 4) code = Statuses.Inconsistency;
                else code = _kdErrors.Values.Where(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase))
                                .Select(e => e.Status).FirstOrDefault() ?? Statuses.SyncError;
                if (tr != null) { tr.Path = path; Complete(tr, t, code); }
                else RecordSyncError(t, path, dir, code);
            }
        }

        EngineInfo GuiEngineStatus(DateTime now, bool hasActive)
        {
            var info = new EngineInfo();
            if (_networkDown) { info.State = EngineState.Offline; info.Since = _pausedAt; }
            else if (_guiSyncStatus == 4 || _guiSyncStatus == 5) { info.State = EngineState.Paused; info.Since = _guiStatusSince; }
            else if (_guiSyncStatus == 6 || _guiSyncStatus == 7) { info.State = EngineState.Stopped; info.Since = _guiStatusSince; }
            else if (_guiSyncStatus == 8) { info.State = EngineState.Error; info.Since = _guiStatusSince; }
            else if (hasActive || (_guiSyncStatus == 2 && _guiSyncStep == 9)) info.State = EngineState.Syncing;
            else if (_guiBusy) { info.State = EngineState.Scanning; info.Since = _guiBusySince; }
            else if ((now - _lastScan).TotalSeconds < 15) { info.State = EngineState.Scanning; info.Since = _scanSince; }
            else info.State = EngineState.UpToDate;

            if (info.State == EngineState.Syncing && (_passFiles > 0 || _passBytes > 0))
            {
                info.PassDone = (int)_passFilesDone;
                info.PassTotal = (int)_passFiles;
                info.BytesDone = _passBytesDone;
                info.BytesTotal = _passBytes;
                if (_passEtaMs > 0) info.EtaSeconds = _passEtaMs / 1000.0;
            }
            return info;
        }

        public List<TransferView> KDriveErrorViews() =>
            _kdErrors.Values.OrderByDescending(e => e.Time).Select(e => new TransferView
            {
                Id = -e.DbId,
                Name = e.Path != null ? System.IO.Path.GetFileName(e.Path) : e.Status,
                Path = e.Path,
                Status = e.Status,
                Start = e.Time,
                End = e.Time,
                Total = -1,
                NoDirection = true,
            }).ToList();

        // ------------------------------------------------------------------ lecture du JSON

        static Dictionary<string, object> Obj(Dictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

        static long Num(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.TryGetValue(key, out var v) || v == null || v is bool) return 0;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); }
            catch (Exception) { return 0; }
        }

        /// <summary>Chaînes encodées en base64 (UTF-8) par kDrive.</summary>
        static string B64(Dictionary<string, object> d, string key)
        {
            if (d == null || !d.TryGetValue(key, out var v) || !(v is string s) || s.Length == 0) return "";
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
            catch (FormatException) { return ""; }
        }

        static string Name(string[] names, int index) => index >= 0 && index < names.Length ? names[index] : index.ToString(CultureInfo.InvariantCulture);
    }
}
