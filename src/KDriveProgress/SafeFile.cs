using System;
using System.IO;
using System.Text;

namespace KDriveProgress
{
    /// <summary>
    /// Écriture résistante aux coupures de courant : fichier temporaire vidé sur le disque,
    /// puis remplacement atomique en gardant l'ancienne version en .bak.
    /// </summary>
    static class SafeFile
    {
        public static void WriteAllText(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            var bytes = new UTF8Encoding(false).GetBytes(text);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true); // FlushFileBuffers : les données sont sur le disque avant le remplacement
            }
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", true);
            else File.Move(tmp, path);
        }

        /// <summary>Lignes du fichier, ou de sa copie .bak si le fichier est absent, vide ou abîmé.</summary>
        public static string[] ReadLines(string path, Func<string[], bool> looksValid)
        {
            foreach (var p in new[] { path, path + ".bak" })
            {
                try
                {
                    if (!File.Exists(p)) continue;
                    var lines = File.ReadAllLines(p, Encoding.UTF8);
                    if (looksValid(lines)) return lines;
                }
                catch (Exception) { }
            }
            return new string[0];
        }
    }
}
