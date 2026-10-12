using System;
using System.Drawing;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace KDriveProgress
{
    /// <summary>Couleurs de l'interface, claires ou sombres selon le réglage « mode des applications » de Windows.</summary>
    static class Theme
    {
        public static readonly bool Dark = DetectDark();

        public static readonly Color Window = Dark ? Hex(0x1F1F1F) : Hex(0xF5F6F8);
        public static readonly Color Surface = Dark ? Hex(0x2B2B2B) : Hex(0xFFFFFF);
        public static readonly Color Hover = Dark ? Hex(0x353535) : Hex(0xF1F3F6);
        public static readonly Color Border = Dark ? Hex(0x3C3C3C) : Hex(0xE3E6EA);
        public static readonly Color Text = Dark ? Hex(0xF2F2F2) : Hex(0x1F2937);
        public static readonly Color Muted = Dark ? Hex(0xA3A3A3) : Hex(0x6B7280);
        public static readonly Color Track = Dark ? Hex(0x404040) : Hex(0xE5E7EB);
        public static readonly Color Up = Dark ? Hex(0x60A5FA) : Hex(0x2563EB);
        public static readonly Color Down = Dark ? Hex(0x4ADE80) : Hex(0x16A34A);
        public static readonly Color Warning = Dark ? Hex(0xFBBF24) : Hex(0xB45309);
        public static readonly Color Ok = Dark ? Hex(0x4ADE80) : Hex(0x16A34A);

        public static Color DirColor(Direction d) => d == Direction.Up ? Up : Down;

        static Color Hex(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

        static bool DetectDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch (Exception) { return false; }
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Barre de titre sombre (Windows 10 20H1 et plus).</summary>
        public static void ApplyTitleBar(IntPtr hwnd)
        {
            if (!Dark) return;
            int on = 1;
            try { DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); } catch (Exception) { }
        }
    }
}
