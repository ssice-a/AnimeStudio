using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AnimeStudio.GUI
{
    static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            if (args?.Length > 0 && string.Equals(args[0], "--export-eiem-prefab", StringComparison.OrdinalIgnoreCase))
            {
                Environment.ExitCode = EndfieldPrefabPackageCommand.Run(args);
                return;
            }
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var main = new MainForm();
            main.Shown += async (_, __) =>
                await ReleaseUpdateChecker.CheckAndPromptAsync(main, manual: false);
            Application.Run(main);
        }
    }
}
