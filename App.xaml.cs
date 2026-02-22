using System.Windows;
using System.Windows.Threading;

namespace DiskPeek;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // UI-thread unhandled exceptions (XAML bindings, event handlers, etc.)
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Background-thread unhandled exceptions (Task.Run, ThreadPool, etc.)
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        // Unobserved task exceptions (fire-and-forget Tasks that threw)
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true; // prevent WPF default crash handler
        ShowErrorDialog(e.Exception);
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            ShowErrorDialog(ex);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved(); // suppress crash; errors are shown in the status bar by callers
    }

    private static void ShowErrorDialog(Exception ex)
    {
        var inner = ex.InnerException is { } ie
            ? $"\n\nCaused by: {ie.GetType().Name}: {ie.Message}"
            : string.Empty;

        MessageBox.Show(
            $"An unexpected error occurred:\n\n{ex.GetType().Name}: {ex.Message}{inner}",
            "DiskPeek — Unexpected Error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
