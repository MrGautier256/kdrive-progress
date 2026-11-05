using System;
using System.Collections.Generic;
using System.IO;

namespace KDriveProgress
{
    /// <summary>
    /// Erreurs que l'utilisateur ne veut plus voir, dans %LOCALAPPDATA%\kDriveProgress\hidden-errors.tsv.
    /// Une entrée vaut pour une erreur précise sur un fichier précis : une autre erreur sur ce fichier reste affichée.
    /// </summary>
    sealed class HiddenErrors
    {
        readonly string _path = Path.Combine(HistoryStore.AppDataDir, "hidden-errors.tsv");
        readonly HashSet<string> _keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public HiddenErrors()
        {
            foreach (var line in SafeFile.ReadLines(_path, lines => true))
                if (line.IndexOf('\t') > 0) _keys.Add(line);
        }

        public int Count => _keys.Count;

        static string Key(TransferView v) => v.Status + "\t" + (v.Path ?? v.Name);

        public bool Contains(TransferView v) => _keys.Contains(Key(v));

        public void Add(TransferView v)
        {
            if (_keys.Add(Key(v))) Save();
        }

        public void Clear()
        {
            _keys.Clear();
            Save();
        }

        void Save()
        {
            try { SafeFile.WriteAllText(_path, string.Join("\n", _keys) + "\n"); }
            catch (Exception) { /* sans gravité : la liste reste valable jusqu'à la fermeture */ }
        }
    }
}
