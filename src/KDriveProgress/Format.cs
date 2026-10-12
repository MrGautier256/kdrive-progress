using System;

namespace KDriveProgress
{
    /// <summary>Tailles, débits, durées et pourcentages dans la langue de l'interface.</summary>
    static class Format
    {
        public static string Size(double bytes)
        {
            if (bytes < 0) return "?";
            if (bytes >= 1L << 30) return string.Format(L.Culture, "{0:N2} {1}", bytes / (1L << 30), L.T("unit.GB"));
            if (bytes >= 1L << 20) return string.Format(L.Culture, "{0:N1} {1}", bytes / (1L << 20), L.T("unit.MB"));
            if (bytes >= 1L << 10) return string.Format(L.Culture, "{0:N0} {1}", bytes / (1L << 10), L.T("unit.KB"));
            return string.Format(L.Culture, "{0:N0} {1}", bytes, L.T("unit.B"));
        }

        /// <summary>Taille ronde pour un libellé de menu (« 100 Mo », « 1 GB »).</summary>
        public static string RoundSize(long bytes)
        {
            if (bytes >= 1L << 30) return string.Format(L.Culture, "{0:0.#} {1}", bytes / (double)(1L << 30), L.T("unit.GB"));
            return string.Format(L.Culture, "{0:0.#} {1}", bytes / (double)(1L << 20), L.T("unit.MB"));
        }

        public static string Rate(double bytesPerSecond) => Size(bytesPerSecond) + "/s";

        public static string Duration(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
            var ts = TimeSpan.FromSeconds(Math.Round(seconds));
            if (ts.TotalHours >= 1) return L.T("fmt.hours", (int)ts.TotalHours, ts.Minutes);
            return string.Format(L.Culture, "{0}:{1:00}", ts.Minutes, ts.Seconds);
        }

        public static string Percent(double fraction) => L.T("fmt.percent", fraction * 100);
    }
}
