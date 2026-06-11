using System.IO;

namespace waktu_sholat
{
    internal static class Program
    {
        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            // Tangkap semua exception agar app tidak diam-diam tertutup.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => LogAndShow(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => LogAndShow(e.ExceptionObject as Exception);

            ApplicationConfiguration.Initialize();
            Application.Run(new Form1());
        }

        private static void LogAndShow(Exception? ex)
        {
            if (ex == null) return;
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "error.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
            }
            catch { /* ignore */ }
            MessageBox.Show(ex.Message, "Waktu Sholat Leeds - Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
