using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KDriveProgress
{
    /// <summary>
    /// Accès « attributs seulement » aux fichiers de kDrive. Ces appels n'ouvrent jamais les données :
    /// ils n'entrent pas en conflit avec le mode de partage de kDrive et ne déclenchent pas d'hydratation.
    /// </summary>
    static class Native
    {
        const uint FILE_READ_ATTRIBUTES = 0x80;
        const uint FILE_SHARE_ALL = 7; // lecture | écriture | suppression
        const uint OPEN_EXISTING = 3;
        const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;
        const int CF_PLACEHOLDER_RANGE_INFO_ONDISK = 1;

        /// <summary>Placeholder dont une partie des données est encore dans le cloud.</summary>
        public const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x400000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileSizeEx(SafeFileHandle h, out long size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetFileAttributesW(string name);

        [DllImport("cldapi.dll")]
        static extern int CfGetPlaceholderRangeInfo(SafeFileHandle h, int infoClass, long startingOffset, long length,
            byte[] buffer, uint bufferLength, out uint returnedLength);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        struct BY_HANDLE_FILE_INFORMATION
        {
            public uint Attributes;
            public long CreationTime, LastAccessTime, LastWriteTime;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);

        /// <summary>Identité NTFS (volume + numéro de fichier) : change quand un fichier est remplacé par un autre du même nom.</summary>
        public static string FileIdentity(SafeFileHandle h)
        {
            if (h == null || h.IsInvalid || !GetFileInformationByHandle(h, out var i)) return null;
            return i.VolumeSerialNumber + ":" + i.FileIndexHigh + ":" + i.FileIndexLow;
        }

        public static string FileIdentity(string path)
        {
            using (var h = OpenAttributesOnly(path)) return FileIdentity(h);
        }

        static bool _cfUnavailable;

        static string Long(string path) =>
            path.Length >= 240 && !path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\" + path : path;

        static SafeFileHandle OpenAttributesOnly(string path) =>
            CreateFileW(Long(path), FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);

        /// <summary>
        /// Taille réelle d'un fichier qu'un autre processus est en train d'écrire
        /// (l'entrée de répertoire, elle, reste périmée). -1 si le fichier n'existe pas.
        /// </summary>
        public static long LiveSize(string path)
        {
            using (var h = OpenAttributesOnly(path))
            {
                if (h.IsInvalid) return -1;
                return GetFileSizeEx(h, out var size) ? size : -1;
            }
        }

        /// <summary>Attributs du fichier, ou null s'il n'existe pas.</summary>
        public static uint? Attributes(string path)
        {
            var a = GetFileAttributesW(Long(path));
            return a == INVALID_FILE_ATTRIBUTES ? (uint?)null : a;
        }

        /// <summary>
        /// Octets d'un placeholder physiquement présents sur le disque (Cloud Files API).
        /// -1 si le fichier n'est pas un placeholder ou si l'API est indisponible.
        /// </summary>
        public static long OnDiskBytes(string path)
        {
            if (_cfUnavailable) return -1;
            try
            {
                using (var h = OpenAttributesOnly(path))
                {
                    if (h.IsInvalid) return -1;
                    var buf = new byte[16 * 1024]; // CF_FILE_RANGE = 2 x Int64
                    long total = 0, offset = 0;
                    for (int pass = 0; pass < 64; pass++)
                    {
                        int hr = CfGetPlaceholderRangeInfo(h, CF_PLACEHOLDER_RANGE_INFO_ONDISK, offset, -1, buf, (uint)buf.Length, out var ret);
                        bool more = hr == unchecked((int)0x800700EA); // ERROR_MORE_DATA
                        if (hr != 0 && !more) return -1;
                        long lastEnd = offset;
                        for (int i = 0; i + 16 <= ret; i += 16)
                        {
                            long start = BitConverter.ToInt64(buf, i);
                            long len = BitConverter.ToInt64(buf, i + 8);
                            total += len;
                            lastEnd = start + len;
                        }
                        if (!more || lastEnd <= offset) break;
                        offset = lastEnd;
                    }
                    return total;
                }
            }
            catch (DllNotFoundException) { _cfUnavailable = true; return -1; }
            catch (EntryPointNotFoundException) { _cfUnavailable = true; return -1; }
        }
    }
}
