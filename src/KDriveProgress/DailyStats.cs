using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace KDriveProgress
{
    /// <summary>
    /// Volumes transférés aujourd'hui, dans %LOCALAPPDATA%\kDriveProgress\today.ini.
    /// Tenu à part de l'historique, qui ne garde que les derniers transferts.
    /// </summary>
    sealed class DailyStats
    {
        public DateTime Day = DateTime.Today;
        public long UpBytes, DownBytes;
        public int UpCount, DownCount;

        readonly string _path = Path.Combine(HistoryStore.AppDataDir, "today.ini");

        public static DailyStats Load(IEnumerable<TransferView> historyForSeed)
        {
            var s = new DailyStats();
            if (File.Exists(s._path))
            {
                try
                {
                    var v = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var line in File.ReadAllLines(s._path, Encoding.UTF8))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) v[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
                    var inv = CultureInfo.InvariantCulture;
                    if (v.TryGetValue("Day", out var d) && DateTime.TryParseExact(d, "yyyy-MM-dd", inv, DateTimeStyles.None, out var day) && day == DateTime.Today)
                    {
                        long.TryParse(v.TryGetValue("UpBytes", out var x) ? x : "0", NumberStyles.Integer, inv, out s.UpBytes);
                        long.TryParse(v.TryGetValue("DownBytes", out x) ? x : "0", NumberStyles.Integer, inv, out s.DownBytes);
                        int.TryParse(v.TryGetValue("UpCount", out x) ? x : "0", NumberStyles.Integer, inv, out s.UpCount);
                        int.TryParse(v.TryGetValue("DownCount", out x) ? x : "0", NumberStyles.Integer, inv, out s.DownCount);
                    }
                    return s;
                }
                catch (Exception) { }
            }
            // Premier lancement : on part de l'historique du jour.
            foreach (var h in historyForSeed) if (h.End.Date == DateTime.Today) s.AddCore(h);
            return s;
        }

        /// <summary>Compte un transfert terminé. Vrai si les totaux ont changé.</summary>
        public bool Add(TransferView v)
        {
            if (v.End.Date != DateTime.Today) return false;
            Roll();
            return AddCore(v);
        }

        bool AddCore(TransferView v)
        {
            if (v.Status != "OK" || v.Total <= 0) return false;
            if (v.Dir == Direction.Up) { UpBytes += v.Total; UpCount++; }
            else { DownBytes += v.Total; DownCount++; }
            return true;
        }

        /// <summary>Remet les compteurs à zéro au changement de jour.</summary>
        public bool Roll()
        {
            if (Day == DateTime.Today) return false;
            Day = DateTime.Today;
            UpBytes = DownBytes = 0;
            UpCount = DownCount = 0;
            return true;
        }

        public string Text
        {
            get
            {
                if (UpCount == 0 && DownCount == 0) return L.T("today.none");
                var parts = new List<string>();
                if (UpCount > 0) parts.Add("↑ " + Format.Size(UpBytes) + " (" + Files(UpCount) + ")");
                if (DownCount > 0) parts.Add("↓ " + Format.Size(DownBytes) + " (" + Files(DownCount) + ")");
                return L.T("today", string.Join("   ·   ", parts));
            }
        }

        static string Files(int n) => L.T(n > 1 ? "files.many" : "files.one", n);

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                var inv = CultureInfo.InvariantCulture;
                SafeFile.WriteAllText(_path,
                    "Day=" + Day.ToString("yyyy-MM-dd", inv) + "\r\n" +
                    "UpBytes=" + UpBytes.ToString(inv) + "\r\nUpCount=" + UpCount.ToString(inv) + "\r\n" +
                    "DownBytes=" + DownBytes.ToString(inv) + "\r\nDownCount=" + DownCount.ToString(inv) + "\r\n");
            }
            catch (Exception) { }
        }
    }
}
