using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace v1per_wpf;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            Log(e.Exception);
            e.Handled = false;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log(e.ExceptionObject as Exception);
    }

    private static void Log(Exception? ex)
    {
        if (ex is null)
            return;
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "v1per_crash.log");
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch (Exception) { }
    }
}