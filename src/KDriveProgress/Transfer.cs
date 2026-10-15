using System;
using System.Collections.Generic;
using System.IO;

namespace KDriveProgress
{
    enum Direction { Up, Down }

    enum TransferKind
    {
        /// <summary>Upload en une seule requête (fichier ≤ 100 Mio).</summary>
        Direct,
        /// <summary>Upload par chunks (« upload session », fichier &gt; 100 Mio).</summary>
        Session,
        Download,
        /// <summary>Vu uniquement par une ligne « Send progress ».</summary>
        Other,
    }

    sealed class Transfer
    {
        public long Id;
        public Direction Dir;
        public TransferKind Kind;
        public string Path;          // chemin complet, s'il est connu
        public string Name;          // nom seul, quand le chemin est inconnu
        public string FileId;        // identifiant kDrive (download, modification)
        public long Total = -1;
        public bool TotalExact;
        public long Done;
        public DateTime Start, End, LastActivity;
        public DateTime LastProgress;   // dernier gain d'octets (détection des transferts bloqués)
        public string Status;        // null tant que le transfert est en cours, puis un code de Statuses ("OK", "http:404"…)
        public string State;         // précision affichée : code "starting" ou "finishing" (clé state.*)

        // Upload session
        public string Uuid;
        public int Chunks, NbChunks, InFlight;
        public long ChunkBytes;
        public bool NameLookupDone;

        // Download
        public int Thread;
        public string RequestId;
        public DateTime HeadersAt;
        public string Tmp;

        readonly List<KeyValuePair<DateTime, long>> _samples = new List<KeyValuePair<DateTime, long>>();

        public bool Active => Status == null;

        public string DisplayName
        {
            get
            {
                if (Path != null) return System.IO.Path.GetFileName(Path.TrimEnd('\\'));
                if (!string.IsNullOrEmpty(Name)) return Name;
                if (FileId != null) return L.T("name.fileId", FileId);
                if (Total > 0) return L.T("name.size", Format.Size(Total));
                return L.T("name.unknown");
            }
        }

        /// <summary>Clé stable d'un transfert terminé, pour ne pas dupliquer l'historique lors d'une relecture du log.</summary>
        public string HistoryKey => Start.ToString("yyyyMMddHHmmssfff") + "|" + (Dir == Direction.Up ? "U" : "D") + "|" + Total
                                    + "|" + (FileId ?? Uuid ?? RequestId ?? Path ?? Name);

        public void Touch(DateTime t)
        {
            if (t > LastActivity) LastActivity = t;
        }

        /// <summary>Nouvelle valeur d'octets transférés (jamais en recul).</summary>
        public void SetDone(long bytes, DateTime t)
        {
            if (Total > 0 && bytes > Total) bytes = Total;
            Touch(t);
            if (bytes <= Done && _samples.Count > 0) return;
            if (bytes > Done && t > LastProgress) LastProgress = t;
            Done = Math.Max(Done, bytes);
            _samples.Add(new KeyValuePair<DateTime, long>(t, Done));
            while (_samples.Count > 2 && (t - _samples[0].Key).TotalSeconds > 20) _samples.RemoveAt(0);
        }

        /// <summary>Débit lissé sur les ~10 dernières secondes, ou null si rien n'a bougé récemment.</summary>
        public double? Rate(DateTime now)
        {
            if (_samples.Count < 2) return null;
            var last = _samples[_samples.Count - 1];
            if ((now - last.Key).TotalSeconds > 15) return null;
            var first = _samples[0];
            for (int i = 0; i < _samples.Count - 1; i++)
            {
                if ((last.Key - _samples[i].Key).TotalSeconds <= 10) { first = _samples[i]; break; }
            }
            if (first.Key == last.Key) first = _samples[Math.Max(0, _samples.Count - 2)];
            double dt = (last.Key - first.Key).TotalSeconds;
            if (dt < 0.4) return null;
            return (last.Value - first.Value) / dt;
        }
    }

    /// <summary>Copie immuable d'un transfert, pour l'interface.</summary>
    sealed class TransferView
    {
        public long Id;
        public Direction Dir;
        public TransferKind Kind;
        public string Name, Path, Status, State, Detail;
        public long Done, Total;
        public double? Fraction, Rate, EtaSeconds;
        public double? StalledSeconds;  // en cours mais sans progrès depuis plus d'une minute
        public bool NoDirection;        // erreur kDrive sans sens connu (pas de flèche)
        public DateTime Start, End;

        public string Folder => Path == null ? null : System.IO.Path.GetDirectoryName(Path);
    }
}
