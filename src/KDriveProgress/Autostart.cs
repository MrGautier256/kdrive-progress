using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace KDriveProgress
{
    /// <summary>Démarrage avec Windows via HKCU\…\Run (aucun droit administrateur nécessaire).</summary>
    static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "kDriveProgress";

        static string Command => "\"" + Application.ExecutablePath + "\" --tray";

        public static bool Enabled
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                        return k?.GetValue(ValueName) is string;
                }
                catch (Exception) { return false; }
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue(ValueName, Command);
                    else k.DeleteValue(ValueName, false);
                }
            }
        }

        /// <summary>Si l'exécutable a été déplacé, la commande enregistrée suit son nouvel emplacement.</summary>
        public static void RefreshPath()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k?.GetValue(ValueName) is string current && !string.Equals(current, Command, StringComparison.OrdinalIgnoreCase))
                        k.SetValue(ValueName, Command);
                }
            }
            catch (Exception) { }
        }
    }
}
