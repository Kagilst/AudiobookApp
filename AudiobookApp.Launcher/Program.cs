using System.Diagnostics;
using System.Runtime.InteropServices;

// Thin root launcher for the portable package. Real app + deps live in App\.
internal static class Program
{
    private const uint MbIconError = 0x00000010;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(nint hWnd, string lpText, string lpCaption, uint uType);

    [STAThread]
    private static int Main()
    {
        var root = AppContext.BaseDirectory;
        var appDir = Path.Combine(root, "App");
        var appExe = Path.Combine(appDir, "AudiobookApp.exe");

        if (!File.Exists(appExe))
        {
            MessageBoxW(
                0,
                "Could not find App\\AudiobookApp.exe.\n\nRe-extract the portable zip so the App folder sits next to this launcher.",
                "AudiobookApp",
                MbIconError);
            return 1;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = appExe,
                WorkingDirectory = appDir,
                UseShellExecute = true
            });
            return process is null ? 1 : 0;
        }
        catch (Exception ex)
        {
            MessageBoxW(0, ex.Message, "AudiobookApp", MbIconError);
            return 1;
        }
    }
}
