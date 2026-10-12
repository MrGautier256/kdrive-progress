using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace KDriveProgress
{
    /// <summary>Réglages de l'app, dans %LOCALAPPDATA%\kDriveProgress\settings.ini (clé=valeur).</summary>
    sealed class Settings
    {
        /// <summary>Notifier la fin des transferts.</summary>
        public bool Notify = true;
        /// <summary>Taille minimale d'un transfert réussi pour mériter une notification (0 = tous).</summary>
        public long NotifyMinBytes = 100L << 20;
        /// <summary>Notifier les transferts en échec, quelle que soit leur taille.</summary>
        public bool NotifyErrors = true;
        /// <summary>Fenêtre toujours au premier plan.</summary>
        public bool TopMost;
        /// <summary>Masquer les fichiers de moins de 1 Mo (listes et compteurs).</summary>
        public bool HideSmall;
        /// <summary>L'utilisateur a refusé l'activation du log étendu : on ne le propose plus au lancement.</summary>
        public bool ExtendedDeclined;

        public const long SmallFileBytes = 1L << 20;
        public long MinVisibleBytes => HideSmall ? SmallFileBytes : 0;

        readonly string _path;

        Settings(string path) { _path = path; }

        public static Settings Load()
        {
            var s = new Settings(Path.Combine(HistoryStore.AppDataDir, "settings.ini"));
            try
            {
                if (!File.Exists(s._path)) return s;
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(s._path, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                }
                if (values.TryGetValue("Notify", out var v) && bool.TryParse(v, out var b)) s.Notify = b;
                if (values.TryGetValue("NotifyErrors", out v) && bool.TryParse(v, out b)) s.NotifyErrors = b;
                if (values.TryGetValue("TopMost", out v) && bool.TryParse(v, out b)) s.TopMost = b;
                if (values.TryGetValue("HideSmall", out v) && bool.TryParse(v, out b)) s.HideSmall = b;
                if (values.TryGetValue("ExtendedDeclined", out v) && bool.TryParse(v, out b)) s.ExtendedDeclined = b;
                if (values.TryGetValue("NotifyMinBytes", out v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) s.NotifyMinBytes = l;
            }
            catch (Exception) { /* réglages illisibles : valeurs par défaut */ }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                SafeFile.WriteAllText(_path,
                    "Notify=" + Notify + "\r\n" +
                    "NotifyErrors=" + NotifyErrors + "\r\n" +
                    "TopMost=" + TopMost + "\r\n" +
                    "HideSmall=" + HideSmall + "\r\n" +
                    "ExtendedDeclined=" + ExtendedDeclined + "\r\n" +
                    "NotifyMinBytes=" + NotifyMinBytes.ToString(CultureInfo.InvariantCulture) + "\r\n");
            }
            catch (Exception) { }
        }
    }
}
