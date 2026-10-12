using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace KDriveProgress
{
    /// <summary>Historique des transferts, dans %LOCALAPPDATA%\kDriveProgress (jamais dans les dossiers de kDrive).</summary>
    sealed class HistoryStore
    {
        public static string AppDataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "kDriveProgress");

        readonly string _path;

        public HistoryStore(string path = null)
        {
            _path = path ?? Path.Combine(AppDataDir, "history.tsv");
        }

        public List<Transfer> Load()
        {
            // Fichier abîmé (coupure de courant) : on retombe sur la copie .bak.
            var lines = SafeFile.ReadLines(_path, ls => ls.Length > 0 && ls.Any(l => Parse(l) != null));
            return lines.Select(Parse).Where(t => t != null).ToList();
        }

        /// <summary>Une ligne de l'historique, ou null si elle est illisible (elle est alors ignorée, pas tout le fichier).</summary>
        static Transfer Parse(string line)
        {
            try
            {
                var f = line.Split('\t');
                if (f.Length < 8) return null;
                var t = new Transfer
                {
                    Start = new DateTime(long.Parse(f[0], CultureInfo.InvariantCulture)),
                    End = new DateTime(long.Parse(f[1], CultureInfo.InvariantCulture)),
                    Dir = f[2] == "U" ? Direction.Up : Direction.Down,
                    Kind = Enum.TryParse(f[3], out TransferKind k) ? k : TransferKind.Other,
                    Total = long.Parse(f[4], CultureInfo.InvariantCulture),
                    Status = LegacyStatus(f[5]),
                    Path = f[6].Length > 0 ? f[6] : null,
                    Name = f[7].Length > 0 ? f[7] : null,
                };
                if (f.Length >= 11)
                {
                    t.FileId = f[8].Length > 0 ? f[8] : null;
                    t.Uuid = f[9].Length > 0 ? f[9] : null;
                    t.RequestId = f[10].Length > 0 ? f[10] : null;
                }
                t.Done = t.Status == "OK" ? t.Total : 0;
                return t;
            }
            catch (Exception) { return null; }
        }

        /// <summary>Les premières versions enregistraient le statut en texte français : on le ramène au code équivalent.</summary>
        static string LegacyStatus(string s)
        {
            switch (s)
            {
                case "interrompu (kDrive a redémarré)": return Statuses.Interrupted;
                case "annulé": return Statuses.Cancelled;
                case "erreur réseau": return Statuses.Network;
                case "perdu de vue": return Statuses.Lost;
                case "erreur": return Statuses.Error;
                case "échec de synchro": return Statuses.SyncError;
            }
            if (s.StartsWith("erreur HTTP ", StringComparison.Ordinal)) return Statuses.Http + ":" + s.Substring(12);
            if (s.StartsWith("échec de synchro : ", StringComparison.Ordinal))
            {
                switch (s.Substring(19))
                {
                    case "un élément du même nom existe déjà": return Statuses.SyncError + ":FileExists";
                    case "accès refusé": return Statuses.SyncError + ":AccessDenied";
                    case "introuvable": return Statuses.SyncError + ":NotFound";
                    case "fichier verrouillé par une autre application": return Statuses.SyncError + ":FileLocked";
                    case "fichier trop volumineux": return Statuses.SyncError + ":FileTooBig";
                    case "espace disque insuffisant": return Statuses.SyncError + ":NotEnoughDiskSpace";
                    case "quota kDrive dépassé": return Statuses.SyncError + ":QuotaExceeded";
                    case "erreur réseau": return Statuses.SyncError + ":NetworkError";
                    default: return Statuses.SyncError;
                }
            }
            return s;
        }

        public void Save(IEnumerable<Transfer> items)
        {
            var sb = new StringBuilder();
            foreach (var t in items.Take(100))
            {
                sb.Append(t.Start.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(t.End.Ticks.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(t.Dir == Direction.Up ? "U" : "D").Append('\t')
                  .Append(t.Kind).Append('\t')
                  .Append(t.Total.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(Clean(t.Status)).Append('\t')
                  .Append(Clean(t.Path)).Append('\t')
                  .Append(Clean(t.Name)).Append('\t')
                  .Append(Clean(t.FileId)).Append('\t')
                  .Append(Clean(t.Uuid)).Append('\t')
                  .Append(Clean(t.RequestId)).Append('\n');
            }
            SafeFile.WriteAllText(_path, sb.ToString());
        }

        static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }
}
