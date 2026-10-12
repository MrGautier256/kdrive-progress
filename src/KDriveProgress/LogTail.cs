using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace KDriveProgress
{
    /// <summary>
    /// Suit le log du moteur kDrive comme « tail -f », sans jamais gêner kDrive :
    /// partage lecture/écriture/suppression, détection de la troncature (rotation à 500 Mio),
    /// lignes incomplètes conservées jusqu'au prochain retour à la ligne.
    /// </summary>
    sealed class LogTail : IDisposable
    {
        static readonly Regex LogName = new Regex(@"^\d{8}_\d{4}_kDrive\.log$", RegexOptions.CultureInvariant);

        readonly FileStream _fs;
        readonly byte[] _buf = new byte[1 << 20];
        readonly char[] _chars;
        Decoder _decoder = Encoding.UTF8.GetDecoder();
        string _partial = "";
        bool _skipFirst;

        public string Path { get; }
        public string Name => System.IO.Path.GetFileName(Path);

        public LogTail(string path)
        {
            Path = path;
            _chars = new char[Encoding.UTF8.GetMaxCharCount(_buf.Length)];
            _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        }

        public static string DefaultDirectory => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kDrive-logdir");

        /// <summary>Logs du moteur (AAAAMMJJ_HHMM_kDrive.log), du plus récent au plus ancien.</summary>
        public static List<string> ListLogs(string dir)
        {
            try
            {
                return Directory.EnumerateFiles(dir, "*_kDrive.log")
                    .Where(f => LogName.IsMatch(System.IO.Path.GetFileName(f)))
                    .OrderByDescending(f => System.IO.Path.GetFileName(f), StringComparer.Ordinal)
                    .ToList();
            }
            catch (IOException) { return new List<string>(); }
            catch (UnauthorizedAccessException) { return new List<string>(); }
        }

        public enum Liveness { Alive, Dead, TooYoung }

        /// <summary>
        /// Relancer kDrive alors qu'il tourne déjà crée un nouveau log de quelques lignes
        /// (« AppServer already running ») pendant que le vrai moteur continue d'écrire dans le sien.
        /// Ce log est « mort » ; un log tout neuf n'est considéré vivant qu'une fois confirmé.
        /// </summary>
        public static Liveness Probe(string path)
        {
            long size = Native.LiveSize(path);
            if (size < 0) return Liveness.Dead;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var buf = new byte[(int)Math.Min(16384, size)];
                    int n = fs.Read(buf, 0, buf.Length);
                    if (Encoding.UTF8.GetString(buf, 0, n).Contains("AppServer already running")) return Liveness.Dead;
                }
            }
            catch (IOException) { return Liveness.TooYoung; }
            if (size >= 16384) return Liveness.Alive;
            return DateTime.Now - File.GetCreationTime(path) >= TimeSpan.FromSeconds(15) ? Liveness.Alive : Liveness.TooYoung;
        }

        /// <summary>
        /// Vrai si le nom désigne maintenant un autre fichier : à 500 Mio, kDrive renomme le log puis en recrée un
        /// du même nom. Notre handle, lui, suit l'ancien fichier renommé.
        /// </summary>
        public bool Replaced()
        {
            var current = Native.FileIdentity(Path);
            var ours = Native.FileIdentity(_fs.SafeFileHandle);
            return current != null && ours != null && current != ours;
        }

        /// <summary>Lit le début du fichier (lignes de démarrage du moteur) sans déplacer la position de lecture.</summary>
        public List<string> ReadHead(int maxBytes)
        {
            using (var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var buf = new byte[Math.Min(maxBytes, Math.Max(0, fs.Length))];
                int n = 0, r;
                while (n < buf.Length && (r = fs.Read(buf, n, buf.Length - n)) > 0) n += r;
                var lines = Encoding.UTF8.GetString(buf, 0, n).Split('\n').ToList();
                if (lines.Count > 0) lines.RemoveAt(lines.Count - 1); // dernière ligne peut-être coupée
                return lines;
            }
        }

        /// <summary>Se place à <paramref name="backlogBytes"/> de la fin pour relire les transferts déjà en cours.</summary>
        public void SeekFromEnd(long backlogBytes)
        {
            long start = Math.Max(0, _fs.Length - backlogBytes);
            _fs.Position = start;
            _skipFirst = start > 0; // on est sans doute tombé au milieu d'une ligne
        }

        /// <summary>Lit tout ce qui a été ajouté depuis le dernier appel et renvoie les lignes retenues par le filtre.</summary>
        public List<string> ReadNew(Func<string, bool> filter, long maxBytes = long.MaxValue)
        {
            var result = new List<string>();
            long len = _fs.Length;
            if (len < _fs.Position)
            {
                // Rotation : kDrive a renommé le fichier puis l'a recréé vide sous le même nom.
                _fs.Position = 0;
                _partial = "";
                _decoder = Encoding.UTF8.GetDecoder();
                _skipFirst = false;
            }
            long read = 0;
            while (_fs.Position < len && read < maxBytes)
            {
                int n = _fs.Read(_buf, 0, (int)Math.Min(_buf.Length, len - _fs.Position));
                if (n <= 0) break;
                read += n;
                int c = _decoder.GetChars(_buf, 0, n, _chars, 0);
                int startIdx = 0;
                for (int i = 0; i < c; i++)
                {
                    if (_chars[i] != '\n') continue;
                    string line = _partial.Length > 0
                        ? _partial + new string(_chars, startIdx, i - startIdx)
                        : new string(_chars, startIdx, i - startIdx);
                    _partial = "";
                    startIdx = i + 1;
                    if (_skipFirst) { _skipFirst = false; continue; }
                    if (line.Length > 0 && line[line.Length - 1] == '\r') line = line.Substring(0, line.Length - 1);
                    if (filter(line)) result.Add(line);
                }
                if (startIdx < c) _partial += new string(_chars, startIdx, c - startIdx);
            }
            return result;
        }

        public void Dispose() => _fs.Dispose();
    }
}
