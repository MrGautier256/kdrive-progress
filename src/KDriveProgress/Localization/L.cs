using System;
using System.Collections.Generic;
using System.Globalization;

namespace KDriveProgress
{
    /// <summary>
    /// Textes de l'interface dans la langue de Windows (anglais par défaut).
    /// Ajouter une langue : créer Lang.xx.cs sur le modèle de Lang.Fr.cs, puis l'enregistrer dans <see cref="Languages"/>.
    /// </summary>
    static partial class L
    {
        /// <summary>Code de langue (ISO 639-1) vers table de textes et culture des nombres.</summary>
        static readonly Dictionary<string, KeyValuePair<Func<Dictionary<string, string>>, string>> Languages =
            new Dictionary<string, KeyValuePair<Func<Dictionary<string, string>>, string>>
            {
                ["en"] = new KeyValuePair<Func<Dictionary<string, string>>, string>(English, "en-US"),
                ["fr"] = new KeyValuePair<Func<Dictionary<string, string>>, string>(French, "fr-FR"),
            };

        static readonly Dictionary<string, string> Fallback = English();
        static readonly Dictionary<string, string> Table;

        /// <summary>Code de la langue choisie (« fr », « en »…).</summary>
        public static readonly string Language;

        /// <summary>Culture des nombres et des dates, cohérente avec la langue affichée.</summary>
        public static readonly CultureInfo Culture;

        static L()
        {
            // KDRIVE_PROGRESS_LANG=en|fr force une langue (tests, ou préférence différente de celle de Windows)
            var code = (Environment.GetEnvironmentVariable("KDRIVE_PROGRESS_LANG") ?? "").Trim().ToLowerInvariant();
            if (!Languages.ContainsKey(code)) code = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            if (!Languages.ContainsKey(code)) code = "en";
            Language = code;
            Table = Languages[code].Key();
            Culture = CultureInfo.GetCultureInfo(Languages[code].Value);
        }

        /// <summary>Texte traduit ; à défaut l'anglais ; à défaut la clé elle-même (repérable à l'écran).</summary>
        public static string T(string key)
        {
            if (Table.TryGetValue(key, out var s)) return s;
            if (Fallback.TryGetValue(key, out s)) return s;
            return key;
        }

        public static string T(string key, params object[] args) => string.Format(Culture, T(key), args);

        /// <summary>Statut d'un transfert terminé (code interne) en texte lisible.</summary>
        public static string Status(string status)
        {
            if (string.IsNullOrEmpty(status)) return "";
            if (status == Statuses.Ok) return "OK";
            if (status.StartsWith(Statuses.Http + ":", StringComparison.Ordinal)) return T("status.http", status.Substring(Statuses.Http.Length + 1));
            if (status.StartsWith(Statuses.SyncError, StringComparison.Ordinal))
            {
                var cause = status.Length > Statuses.SyncError.Length + 1 ? status.Substring(Statuses.SyncError.Length + 1) : null;
                if (cause == null) return T("status.syncerror");
                var key = "cause." + cause;
                return T("status.syncerror.cause", Table.ContainsKey(key) || Fallback.ContainsKey(key) ? T(key) : cause);
            }
            var k = "status." + status;
            if (Table.ContainsKey(k) || Fallback.ContainsKey(k)) return T(k);
            return status; // historique d'une ancienne version : texte déjà rédigé
        }
    }

    /// <summary>Codes de statut enregistrés dans l'historique (indépendants de la langue).</summary>
    static class Statuses
    {
        public const string Ok = "OK";
        public const string Interrupted = "interrupted";
        public const string Cancelled = "cancelled";
        public const string Http = "http";            // http:404
        public const string Network = "network";
        public const string Lost = "lost";
        public const string Error = "error";
        public const string SyncError = "syncerror";  // syncerror:FileExists

        public static bool IsInterrupted(string s) =>
            s == Interrupted || (s != null && s.StartsWith("interrompu", StringComparison.Ordinal));
    }
}
