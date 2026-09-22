using System;
using System.IO;

namespace Klyro_App;

public static class Program
{
    [System.STAThread]
    public static void Main(string[] args)
    {
        string logPath = Path.Combine(AppContext.BaseDirectory, "boot.log");
        try
        {
            File.AppendAllText(logPath, $"[{DateTime.UtcNow:O}] 1. Main entered. Args: {string.Join(" ", args)}\n");
            
            WinRT.ComWrappersSupport.InitializeComWrappers();
            File.AppendAllText(logPath, $"[{DateTime.UtcNow:O}] 2. ComWrappers initialized.\n");

            Microsoft.UI.Xaml.Application.Start((p) =>
            {
                try
                {
                    File.AppendAllText(logPath, $"[{DateTime.UtcNow:O}] 3. Inside Application.Start\n");
                    var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                    System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                    new App();
                    File.AppendAllText(logPath, $"[{DateTime.UtcNow:O}] 4. App instantiated successfully.\n");
                }
                catch (Exception exStart)
                {
                    File.AppendAllText(logPath, $"[{DateTime.UtcNow:O}] [Application.Start callback Exception] {exStart}\n");
                }
            });

            File.AppendAllText(logPath, $"[{DateTime.UtcNow:O}] 5. Application.Start returned normally.\n");
        }
        catch (Exception ex)
        {
            File.AppendAllText(logPath, $"[{DateTime.UtcNow:O}] [Fatal Main Exception] {ex}\n");
        }
    }
}
