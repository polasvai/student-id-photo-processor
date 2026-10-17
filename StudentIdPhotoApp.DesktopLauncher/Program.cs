using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows.Forms;

namespace StudentIdPhotoApp.DesktopLauncher;

static class Program
{
    private const string AppUrl = "http://localhost:5209";
    private static Process? _serverProcess;
    private static NotifyIcon? _trayIcon;

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var serverExePath = Path.Combine(baseDir, "StudentIdPhotoApp.exe");

        if (!File.Exists(serverExePath))
        {
            serverExePath = Path.Combine(baseDir, "app", "StudentIdPhotoApp.exe");
        }

        if (!File.Exists(serverExePath))
        {
            MessageBox.Show(
                $"Could not locate 'StudentIdPhotoApp.exe' in:\n{baseDir}",
                "Startup Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        var logPath = Path.Combine(baseDir, "server.log");

        // 1. Start server process if not already responding
        if (!IsServerRunning())
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = serverExePath,
                    Arguments = "--no-browser", // DesktopLauncher will handle opening the window
                    WorkingDirectory = Path.GetDirectoryName(serverExePath) ?? baseDir,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                // Clear previous log
                try { File.Delete(logPath); } catch { }

                _serverProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };

                _serverProcess.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        try { File.AppendAllText(logPath, e.Data + Environment.NewLine); } catch { }
                    }
                };

                _serverProcess.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        try { File.AppendAllText(logPath, "[ERROR] " + e.Data + Environment.NewLine); } catch { }
                    }
                };

                _serverProcess.Start();
                _serverProcess.BeginOutputReadLine();
                _serverProcess.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to start background server:\n{ex.Message}",
                    "Startup Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }
        }

        // 2. Wait for server to become responsive
        var ready = WaitForServer(TimeSpan.FromSeconds(20));
        if (!ready)
        {
            string errSummary = "";
            if (File.Exists(logPath))
            {
                try
                {
                    var lines = File.ReadAllLines(logPath);
                    errSummary = "\n\nServer log excerpt:\n" + string.Join("\n", lines.Length > 8 ? lines[^8..] : lines);
                }
                catch { }
            }

            var dialogResult = MessageBox.Show(
                $"Server did not respond at {AppUrl} within 20 seconds.{errSummary}\n\nWould you like to try opening the app anyway?",
                "Server Warning",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (dialogResult == DialogResult.No)
            {
                CleanupServer();
                return;
            }
        }

        // 3. Open application window
        OpenBrowserWindow();

        // 4. Setup System Tray Icon so application stays alive reliably
        SetupTrayIcon(baseDir, logPath);

        // Ensure server is killed when launcher terminates
        AppDomain.CurrentDomain.ProcessExit += (s, e) => CleanupServer();
        Application.ApplicationExit += (s, e) => CleanupServer();

        Application.Run();
    }

    private static void OpenBrowserWindow()
    {
        try
        {
            var edgePsi = new ProcessStartInfo
            {
                FileName = "msedge.exe",
                Arguments = $"--app={AppUrl} --window-size=1300,900",
                UseShellExecute = true
            };
            Process.Start(edgePsi);
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = AppUrl,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }

    private static void SetupTrayIcon(string baseDir, string logPath)
    {
        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Open Photo Processor", null, (s, e) => OpenBrowserWindow());
        contextMenu.Items.Add("View Server Log", null, (s, e) =>
        {
            if (File.Exists(logPath))
            {
                Process.Start(new ProcessStartInfo("notepad.exe", logPath) { UseShellExecute = true });
            }
            else
            {
                MessageBox.Show("No log file found yet.", "Server Log", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        });
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Exit & Stop Server", null, (s, e) =>
        {
            CleanupServer();
            _trayIcon?.Dispose();
            Application.Exit();
        });

        _trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            ContextMenuStrip = contextMenu,
            Text = "Student ID Photo Processor (Running)",
            Visible = true
        };

        _trayIcon.DoubleClick += (s, e) => OpenBrowserWindow();
    }

    private static void CleanupServer()
    {
        try
        {
            if (_serverProcess != null && !_serverProcess.HasExited)
            {
                _serverProcess.Kill(entireProcessTree: true);
                _serverProcess.Dispose();
                _serverProcess = null;
            }
        }
        catch { }
    }

    private static bool IsServerRunning()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
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
