using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Serilog;

namespace CurePlease;

/// <summary>
/// Bootstraps file logging and global exception capture. Everything the UI log
/// shows is mirrored to a daily rolling file under <exe folder>\logs so
/// post-mortems do not depend on copying the on-screen log.
/// </summary>
public partial class App : Application
{
    /// <summary>Logs live next to the exe (not %appdata%) so a session's log is found where the app is.</summary>
    public static string LogDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        Directory.CreateDirectory(LogDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(LogDirectory, "cureplease-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("CurePlease {Version} starting", typeof(App).Assembly.GetName().Version);

        DispatcherUnhandledException += (sender, args) =>
        {
            Log.Fatal(args.Exception, "Unhandled UI exception");
        };

        AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "Unhandled AppDomain exception");
        };

        TaskScheduler.UnobservedTaskException += (sender, args) =>
        {
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("CurePlease exiting");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
