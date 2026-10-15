using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace KDriveProgress
{
    /// <summary>Release publiée sur GitHub, plus récente que la version en cours.</summary>
    sealed class UpdateInfo
    {
        public Version Version;
        public string Tag, PageUrl, ExeUrl, ShaUrl;
    }

    /// <summary>
    /// Mise à jour de l'app depuis les releases GitHub du dépôt.
    /// Vérification : API publique « releases/latest » (sans compte, pré-versions exclues).
    /// Installation : téléchargement de l'exe et de son empreinte SHA-256, vérification, puis remplacement :
    /// un exe en cours ne peut pas être écrasé mais peut être renommé (en .old) ; le nouvel exe prend sa place,
    /// est lancé, et supprime le .old à son démarrage.
    /// </summary>
    static class Updater
    {
        public const string Repository = "MrGautier256/kdrive-progress";
        const string LatestApi = "https://api.github.com/repos/" + Repository + "/releases/latest";
        public const string ReleasesPage = "https://github.com/" + Repository + "/releases";
        const string ExeAsset = "KDriveProgress.exe";

        public static Version Current
        {
            get
            {
                var v = typeof(Updater).Assembly.GetName().Version;
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        static string UpdateDir => Path.Combine(HistoryStore.AppDataDir, "update");

        static WebClient Client()
        {
            // .NET Framework 4.8 : TLS 1.2 explicite pour GitHub
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "kDriveProgress/" + Current.ToString(3);
            wc.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
            return wc;
        }

        /// <summary>Release plus récente que la version en cours, ou null. Lève une exception si GitHub est injoignable.</summary>
        public static UpdateInfo Check()
        {
            string json;
            using (var wc = Client()) json = wc.DownloadString(LatestApi);
            var root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            if (root == null) return null;
            var tag = root.TryGetValue("tag_name", out var t) ? t as string : null;
            if (string.IsNullOrEmpty(tag) || (root.TryGetValue("prerelease", out var pre) && pre is bool b && b)) return null;
            // « v2.1.0 » ou « v2.1.0-beta.1 » : seule la partie numérique compte
            var numeric = tag.TrimStart('v', 'V').Split('-')[0];
            if (!Version.TryParse(numeric, out var version)) return null;
            version = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
            if (version <= Current) return null;

            var info = new UpdateInfo
            {
                Version = version,
                Tag = tag,
                PageUrl = root.TryGetValue("html_url", out var page) ? page as string : ReleasesPage,
            };
            if (root.TryGetValue("assets", out var assets) && assets is object[] list)
                foreach (var a in list.OfType<Dictionary<string, object>>())
                {
                    var name = a.TryGetValue("name", out var n) ? n as string : null;
                    var url = a.TryGetValue("browser_download_url", out var u) ? u as string : null;
                    if (name == ExeAsset) info.ExeUrl = url;
                    else if (name == ExeAsset + ".sha256") info.ShaUrl = url;
                }
            return info.ExeUrl != null && info.ShaUrl != null ? info : null;
        }

        /// <summary>L'app peut-elle remplacer son propre exe (dossier accessible en écriture) ?</summary>
        public static bool CanReplaceSelf()
        {
            try
            {
                var dir = Path.GetDirectoryName(Application.ExecutablePath);
                var probe = Path.Combine(dir, ".kdp-write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>Télécharge l'exe de la release et vérifie son empreinte SHA-256. Renvoie le chemin du fichier vérifié.</summary>
        public static string Download(UpdateInfo info)
        {
            Directory.CreateDirectory(UpdateDir);
            var target = Path.Combine(UpdateDir, "KDriveProgress-" + info.Version.ToString(3) + ".exe");
            string expected;
            using (var wc = Client())
            {
                expected = wc.DownloadString(info.ShaUrl).Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
                wc.Headers[HttpRequestHeader.Accept] = "application/octet-stream";
                wc.DownloadFile(info.ExeUrl, target);
            }
            string actual;
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(target))
                actual = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
            if (actual != expected)
            {
                File.Delete(target);
                throw new InvalidDataException("SHA-256");
            }
            return target;
        }

        /// <summary>
        /// Met le nouvel exe à la place du courant et le lance. L'appelant doit ensuite quitter l'app :
        /// la nouvelle instance attend la fin de l'ancienne avant de démarrer.
        /// </summary>
        public static void Apply(string newExe, bool startHidden)
        {
            var current = Application.ExecutablePath;
            var old = current + ".old";
            if (File.Exists(old)) File.Delete(old);
            File.Move(current, old); // permis même si l'exe est en cours d'exécution
            try
            {
                File.Copy(newExe, current);
            }
            catch (Exception)
            {
                File.Move(old, current); // retour à l'état initial
                throw;
            }
            var args = "--updated " + Current.ToString(3) + (startHidden ? " --tray" : "");
            Process.Start(new ProcessStartInfo(current, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(current) })?.Dispose();
        }

        /// <summary>Au démarrage : supprime l'ancien exe et les téléchargements laissés par une mise à jour.</summary>
        public static void Cleanup()
        {
            var old = Application.ExecutablePath + ".old";
            for (int i = 0; i < 20 && File.Exists(old); i++)
            {
                try { File.Delete(old); }
                catch (Exception) { System.Threading.Thread.Sleep(250); } // l'ancienne instance finit de se fermer
            }
            try { if (Directory.Exists(UpdateDir)) Directory.Delete(UpdateDir, true); } catch (Exception) { }
        }

        public static void OpenReleasePage(string url)
        {
            try { Process.Start(url ?? ReleasesPage)?.Dispose(); } catch (Exception) { }
        }
    }
}
