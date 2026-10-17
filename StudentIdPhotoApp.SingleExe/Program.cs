using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;

namespace StudentIdPhotoApp.SingleExe;

static class Program
{
    private const string Version = "1.0.0";

    [STAThread]
    static void Main()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var targetDir = Path.Combine(appData, "StudentIdPhotoApp");
            var launcherExe = Path.Combine(targetDir, "Launch-StudentIdPhotoApp.exe");
            var markerFile = Path.Combine(targetDir, $".version_{Version}");

            // Extract embedded portable package on first run or version update
            if (!File.Exists(launcherExe) || !File.Exists(markerFile))
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

            if (File.Exists(launcherExe))
            {
                var psi = new ProcessStartInfo
                {
                    FileName = launcherExe,
                    WorkingDirectory = targetDir,
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
            else
            {
                System.Windows.Forms.MessageBox.Show(
                    $"Unable to find executable in:\n{targetDir}",
                    "Startup Error",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            System.Windows.Forms.MessageBox.Show(
                $"Failed to launch Student ID Photo Processor:\n{ex.Message}",
                "Fatal Error",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
        }
    }
}
