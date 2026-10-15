using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace KDriveProgress
{
    static class Program
    {
        const string MutexName = @"Local\KDriveProgress.Instance";
        const string ShowEventName = @"Local\KDriveProgress.Show";
        const string QuitEventName = @"Local\KDriveProgress.Quit";

        [STAThread]
        static int Main(string[] args)
        {
            // Outils : --replay <log> <sortie.txt> (diagnostic), --make-icon <fichier.ico> (construction)
            if (args.Length >= 3 && args[0] == "--replay") { Engine.Replay(args[1], args[2]); return 0; }
            if (args.Length >= 2 && args[0] == "--make-icon") { Icons.WriteIco(args[1]); return 0; }
            if (args.Length >= 1 && args[0] == "--quit") return QuitRunningInstance();

            // --updated <ancienne version> : lancé par une mise à jour, l'ancienne instance est en train de se fermer.
            int u = Array.IndexOf(args, "--updated");
            string updatedFrom = u >= 0 && u + 1 < args.Length ? args[u + 1] : null;

            using (var mutex = new Mutex(false, MutexName))
            {
                bool owned;
                try { owned = mutex.WaitOne(updatedFrom != null ? 30000 : 0); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned)
                {
                    // Déjà lancée : on demande à l'instance existante d'afficher sa fenêtre.
                    try { using (var ev = EventWaitHandle.OpenExisting(ShowEventName)) ev.Set(); } catch (Exception) { }
                    return 0;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Autostart.RefreshPath();
                Updater.Cleanup();

                bool hidden = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
                using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName))
                using (var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName))
                {
                    var context = new TrayContext(hidden, updatedFrom);
                    var sync = new WindowsFormsSynchronizationContext();
                    var waitShow = ThreadPool.RegisterWaitForSingleObject(showEvent,
                        (s, timedOut) => sync.Post(_ => context.ShowForm(), null), null, Timeout.Infinite, false);
                    var waitQuit = ThreadPool.RegisterWaitForSingleObject(quitEvent,
                        (s, timedOut) => sync.Post(_ => context.Quit(), null), null, Timeout.Infinite, true);
                    Application.Run(context);
                    waitShow.Unregister(null);
                    waitQuit.Unregister(null);
                }
                mutex.ReleaseMutex();
            }
            return 0;
        }

        /// <summary>
        /// KDriveProgress.exe --quit : demande à l'instance en cours de se fermer proprement (historique enregistré)
        /// et attend jusqu'à 10 s qu'elle ait disparu. Code 0 si plus aucune instance ne tourne.
        /// </summary>
        static int QuitRunningInstance()
        {
            try { using (var ev = EventWaitHandle.OpenExisting(QuitEventName)) ev.Set(); }
            catch (WaitHandleCannotBeOpenedException) { return 0; } // rien ne tourne
            try
            {
                using (var m = Mutex.OpenExisting(MutexName))
                {
                    try
                    {
                        if (!m.WaitOne(10000)) return 1;
                        m.ReleaseMutex();
                    }
                    catch (AbandonedMutexException) { }
                }
            }
            catch (WaitHandleCannotBeOpenedException) { }
            return 0;
        }
    }
}
