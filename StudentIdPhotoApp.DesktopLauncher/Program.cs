using System.Diagnostics;

namespace StudentIdPhotoApp.DesktopLauncher;

static class Program
{
    private const string AppUrl = "http://localhost:5209";

    [STAThread]
    static void Main()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var serverExePath = Path.Combine(baseDir, "StudentIdPhotoApp.exe");

        // If not in same folder, check "app" subfolder
        if (!File.Exists(serverExePath))
        {
            serverExePath = Path.Combine(baseDir, "app", "StudentIdPhotoApp.exe");
        }

        if (!File.Exists(serverExePath))
        {
            // If running dotnet dll
            var dllPath = Path.Combine(baseDir, "StudentIdPhotoApp.dll");
            if (!File.Exists(dllPath))
            {
                System.Windows.Forms.MessageBox.Show(
                    $"Could not locate 'StudentIdPhotoApp.exe' in:\n{baseDir}",
                    "Startup Error",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
                return;
            }
        }

        Process? serverProcess = null;

        // Check if server is already running
        if (!IsServerRunning())
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = serverExePath,
                    WorkingDirectory = Path.GetDirectoryName(serverExePath) ?? baseDir,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = false
                };

                serverProcess = Process.Start(psi);
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(
                    $"Failed to start background server:\n{ex.Message}",
                    "Startup Error",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
                return;
            }
        }

        // Wait for server to become responsive
        var ready = WaitForServer(TimeSpan.FromSeconds(15));
        if (!ready)
        {
            System.Windows.Forms.MessageBox.Show(
                "Timed out waiting for local server to respond at " + AppUrl,
                "Warning",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Warning);
        }

        // Open application in Desktop Window mode (Chromeless Edge App Window)
        Process? browserProcess = null;
        try
        {
            var edgePsi = new ProcessStartInfo
            {
                FileName = "msedge.exe",
                Arguments = $"--app={AppUrl} --window-size=1300,900",
                UseShellExecute = true
            };
            browserProcess = Process.Start(edgePsi);
        }
        catch
        {
            // Fallback to default browser
            try
            {
                browserProcess = Process.Start(new ProcessStartInfo
                {
                    FileName = AppUrl,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        // When the browser window closes and we launched the server process, clean up
        if (browserProcess != null && serverProcess != null && !serverProcess.HasExited)
        {
            try
            {
                browserProcess.WaitForExit();
            }
            catch { }

            try
            {
                if (!serverProcess.HasExited)
                {
                    serverProcess.Kill(entireProcessTree: true);
                }
            }
            catch { }
        }
    }

    private static bool IsServerRunning()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            var res = client.GetAsync($"{AppUrl}/api/batch/models/status").GetAwaiter().GetResult();
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static bool WaitForServer(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };

        while (sw.Elapsed < timeout)
        {
            try
            {
                var res = client.GetAsync($"{AppUrl}/api/batch/models/status").GetAwaiter().GetResult();
                if (res.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch { }

            Thread.Sleep(300);
        }

        return false;
    }
}
