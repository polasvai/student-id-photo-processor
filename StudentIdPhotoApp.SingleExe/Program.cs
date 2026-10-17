using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace StudentIdPhotoApp.SingleExe;

static class Program
{
    private const string Version = "1.0.1";
    private const string AppUrl = "http://localhost:5209";
    private static Process? _serverProcess;
    private static NotifyIcon? _trayIcon;

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var targetDir = Path.Combine(appData, "StudentIdPhotoApp");
            var serverExePath = Path.Combine(targetDir, "StudentIdPhotoApp.exe");
            var markerFile = Path.Combine(targetDir, $".version_{Version}");
            var logPath = Path.Combine(targetDir, "server.log");

            // 1. Extract embedded portable package on first run or version update
            if (!File.Exists(serverExePath) || !File.Exists(markerFile))
            {
                Directory.CreateDirectory(targetDir);
                var assembly = Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream("package.zip");
                if (stream != null)
                {
                    using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                    archive.ExtractToDirectory(targetDir, overwriteFiles: true);
                    File.WriteAllText(markerFile, Version);
                }
            }

            if (!File.Exists(serverExePath))
            {
                MessageBox.Show(
                    $"Unable to find 'StudentIdPhotoApp.exe' in:\n{targetDir}",
                    "Startup Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            // 2. Start server process if not already responding
            if (!IsServerRunning())
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = serverExePath,
                        Arguments = "--no-browser", // SingleExe handles the browser window
                        WorkingDirectory = targetDir,
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

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

            // 3. Wait for server to become responsive
            var ready = WaitForServer(TimeSpan.FromSeconds(20));
            if (!ready)
            {
                string errSummary = "";
                if (File.Exists(logPath))
                {
                    try
                    {
                        var lines = File.ReadAllLines(logPath);
                        errSummary = "\n\nServer log:\n" + string.Join("\n", lines.Length > 8 ? lines[^8..] : lines);
                    }
                    catch { }
                }

                var dialogResult = MessageBox.Show(
                    $"Server is taking longer than expected to start at {AppUrl}.{errSummary}\n\nWould you like to try opening anyway?",
                    "Server Notice",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);

                if (dialogResult == DialogResult.No)
                {
                    CleanupServer();
                    return;
                }
            }

            // 4. Open application in Desktop Window mode
            OpenBrowserWindow();

            // 5. Setup System Tray Icon
            SetupTrayIcon(targetDir, logPath);

            // Cleanup server on exit
            AppDomain.CurrentDomain.ProcessExit += (s, e) => CleanupServer();
            Application.ApplicationExit += (s, e) => CleanupServer();

            Application.Run();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Fatal error in Student ID Photo Processor:\n{ex.Message}",
                "Fatal Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
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
