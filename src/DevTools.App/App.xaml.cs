using System.Windows;
using System.Security.Principal;

namespace DevTools.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            NoticeWindow.Show("Administrator access required", "Start DevTools.exe to approve administrator access.");
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, args) => { NoticeWindow.Show("DevTools error", args.Exception.Message); args.Handled = true; };
        AppModuleContext context;
        try
        {
            context = e.Args.Length == 4 && e.Args[0] == "--resume-updates" && e.Args[2] == "--data-directory"
                ? new AppModuleContext(System.IO.Path.GetFullPath(e.Args[3])) : new AppModuleContext();
        }
        catch (Exception error)
        {
            NoticeWindow.Show("Portable data unavailable", "DevTools needs a writable Data folder beside the app. Move the complete app folder to a writable drive and try again.\n\n" + error.Message);
            Shutdown(1); return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--resume-updates")
        {
            if (e.Args.Length is not (2 or 4) || !Guid.TryParse(e.Args[1], out var token)) { Shutdown(2); return; }
            try { var resumed = await DevTools.Modules.Windows.UpdatePauseController.Create(context).RestoreDueAsync(token, DateTimeOffset.UtcNow); if (!resumed) resumed = await DevTools.Modules.Windows.UpdatePolicyController.Create(context).RestoreDueAsync(token, DateTimeOffset.UtcNow); if (!resumed) resumed = await DevTools.Modules.Windows.UpdateController.Create(context).ResumeDueAsync(token, DateTimeOffset.UtcNow, CancellationToken.None); context.Log("Update recovery", resumed ? "Previous Windows Update settings restored." : "No matching due recovery plan; no changes made."); Shutdown(0); }
            catch (Exception error) { context.Log("Update recovery", error.Message); Shutdown(1); }
            return;
        }
        try { await new DevTools.Modules.Windows.UpdateResumeScheduler(context.DataDirectory).RebindPortableLocationAsync(CancellationToken.None); }
        catch (Exception error) { context.Log("Update recovery", error.Message); NoticeWindow.Show("Update recovery needs attention", "A saved update-recovery plan could not be linked to this app location. Keep the previous copy available and inspect Update control.\n\n" + error.Message); }
        var shell = new ShellViewModel(context);
        context.SettingsChanged += shell.ApplyTheme;
        var window = new MainWindow { DataContext = shell };
        MainWindow = window;
        window.Closing += (_, args) => { if (context.IsOperationRunning) { NoticeWindow.Show("Operation running", "Cancel the active operation and wait for it to finish before closing."); args.Cancel = true; } };
        window.Closed += (_, _) => shell.Dispose();
        shell.ApplyTheme();
        window.Show();
    }
}
