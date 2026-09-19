using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ProjectDoorango;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--self-test")
            return LauncherChecks.Run(args[1]).GetAwaiter().GetResult();
        string root = AppContext.BaseDirectory;
        for (var dir = new DirectoryInfo(root); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "server"))) { root = dir.FullName; break; }
        if (args.Length == 2 && args[0] == "--root") root = Path.GetFullPath(args[1]);
        if (args.Length == 2 && args[0] == "--server-smoke")
            return LauncherChecks.ServerSmoke(root, args[1]).GetAwaiter().GetResult();
        if (args.Length == 2 && args[0] == "--download-smoke")
            return LauncherChecks.DownloadSmoke(root, args[1]).GetAwaiter().GetResult();
        bool preview = args.Length >= 2 && args[0] == "--preview";
        if (preview && args.Length > 3) root = Path.GetFullPath(args[3]);
        var app = new Application();
        app.DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show(e.Exception.Message, "ProjectDoorango", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        var window = new MainWindow(root, preview);
        if (preview && args.Length > 2) window.ShowPage(args[2]);
        if (preview)
            window.ContentRendered += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                var content = (FrameworkElement)window.Content;
                var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var output = File.Create(Path.GetFullPath(args[1]))) encoder.Save(output);
                window.Close();
            }));
        app.Run(window);
        return 0;
    }
}
