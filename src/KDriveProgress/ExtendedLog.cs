using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace KDriveProgress
{
    /// <summary>
    /// Activation du log étendu sans toucher à kDrive : kDrive 3.8 lit la variable d'environnement
    /// KDRIVE_FORCE_EXTENDED_LOG=1 à son démarrage (chaîne vérifiée dans kDrive.exe 3.8.7.1).
    /// On la définit pour le compte de l'utilisateur (HKCU\Environment) ; elle s'applique au prochain lancement de kDrive.
    /// </summary>
    static class ExtendedLog
    {
        public const string Variable = "KDRIVE_FORCE_EXTENDED_LOG";

        public static bool VariableSet =>
            Environment.GetEnvironmentVariable(Variable, EnvironmentVariableTarget.User) == "1";

        /// <summary>Définit ou retire la variable (Windows prévient l'Explorateur, qui la transmettra aux programmes lancés ensuite).</summary>
        public static void SetVariable(bool on)
        {
            Environment.SetEnvironmentVariable(Variable, on ? "1" : null, EnvironmentVariableTarget.User);
            Environment.SetEnvironmentVariable(Variable, on ? "1" : null); // pour un kDrive lancé par l'app elle-même
        }

        /// <summary>Chemin de kDrive.exe : celui du processus vu en dernier, sinon celui de l'installation.</summary>
        public static string KDriveExe(string lastSeen)
        {
            // kDrive 4 : ne jamais prendre le lanceur de l'interface (client\kDrive.exe) pour le moteur
            if (!string.IsNullOrEmpty(lastSeen) && File.Exists(lastSeen)
                && lastSeen.IndexOf(@"\client\", StringComparison.OrdinalIgnoreCase) < 0) return lastSeen;
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                try
                {
                    using (var k = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"))
                    {
                        if (k == null) continue;
                        foreach (var name in k.GetSubKeyNames())
                            using (var app = k.OpenSubKey(name))
                            {
                                if (!(app?.GetValue("DisplayName") is string dn) || dn != "kDrive") continue;
                                if (app.GetValue("InstallLocation") is string dir)
                                {
                                    var exe = Path.Combine(dir.Trim('"'), "kDrive.exe");
                                    if (File.Exists(exe)) return exe;
                                }
                            }
                    }
                }
                catch (Exception) { }
            }
            var def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "kDrive", "kDrive.exe");
            return File.Exists(def) ? def : null;
        }

        /// <summary>Lance kDrive (jamais de fermeture : c'est à l'utilisateur de le quitter).</summary>
        public static bool StartKDrive(string lastSeen)
        {
            var exe = KDriveExe(lastSeen);
            if (exe == null) return false;
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) };
            if (VariableSet) psi.EnvironmentVariables[Variable] = "1";
            try { Process.Start(psi)?.Dispose(); return true; }
            catch (Exception) { return false; }
        }
    }
}
