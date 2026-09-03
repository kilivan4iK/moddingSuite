using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace moddingSuite.BL
{
    public static class DiagnosticReport
    {
        public static string Build()
        {
            var sb = new StringBuilder();

            sb.AppendLine("=== Modding Suite diagnostic report ===");
            sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}");
            sb.AppendLine();

            sb.AppendLine("--- Runtime ---");
            sb.AppendLine($"  .NET: {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"  Process arch: {RuntimeInformation.ProcessArchitecture}");
            sb.AppendLine($"  OS: {RuntimeInformation.OSDescription}");
            sb.AppendLine($"  OS arch: {RuntimeInformation.OSArchitecture}");
            sb.AppendLine($"  OS version: {Environment.OSVersion}");
            sb.AppendLine($"  64-bit process: {Environment.Is64BitProcess}");
            sb.AppendLine($"  64-bit OS: {Environment.Is64BitOperatingSystem}");
            sb.AppendLine($"  CLR version: {Environment.Version}");
            sb.AppendLine($"  Processor count: {Environment.ProcessorCount}");
            sb.AppendLine();

            sb.AppendLine("--- App ---");
            var entry = Assembly.GetEntryAssembly();
            sb.AppendLine($"  Entry assembly: {entry?.FullName}");
            sb.AppendLine($"  Location: {entry?.Location}");
            sb.AppendLine($"  Working dir: {Environment.CurrentDirectory}");
            sb.AppendLine();

            sb.AppendLine("--- Settings ---");
            try
            {
                var settingsPath = SettingsManager.SettingsPath;
                sb.AppendLine($"  Settings file: {settingsPath}");
                sb.AppendLine($"  Exists: {File.Exists(settingsPath)}");
                if (File.Exists(settingsPath))
                {
                    var info = new FileInfo(settingsPath);
                    sb.AppendLine($"  Size: {info.Length} bytes");
                    sb.AppendLine($"  Modified: {info.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
                }

                var settings = SettingsManager.Load();
                sb.AppendLine($"  InitialSettings: {settings.InitialSettings}");
                sb.AppendLine($"  WargamePath: {settings.WargamePath}");
                sb.AppendLine($"    Exists: {(string.IsNullOrEmpty(settings.WargamePath) ? "n/a" : Directory.Exists(settings.WargamePath).ToString())}");
                sb.AppendLine($"  SavePath: {settings.SavePath}");
                sb.AppendLine($"    Exists: {(string.IsNullOrEmpty(settings.SavePath) ? "n/a" : Directory.Exists(settings.SavePath).ToString())}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  ERROR reading settings: {ex.Message}");
            }
            sb.AppendLine();

            sb.AppendLine("--- Bundled tools ---");
            try
            {
                var baseDir = Path.GetDirectoryName(entry?.Location) ?? Environment.CurrentDirectory;
                CheckFile(sb, baseDir, "IronPython.dll");
                CheckFile(sb, baseDir, "DynamicLanguageRuntime.dll");
                CheckFile(sb, baseDir, "Microsoft.Scripting.dll");
                CheckFile(sb, baseDir, "K4os.Compression.LZ4.dll");
                CheckFile(sb, baseDir, "ZstdSharp.dll");
                CheckFile(sb, baseDir, "Ionic.Zlib.Core.dll");
                CheckFile(sb, baseDir, Path.Combine("tools", "quickbms_win", "quickbms.exe"));
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  ERROR checking files: {ex.Message}");
            }
            sb.AppendLine();

            sb.AppendLine("--- Environment ---");
            try
            {
                sb.AppendLine($"  TEMP: {Path.GetTempPath()}");
                sb.AppendLine($"  AppData: {Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)}");
                sb.AppendLine($"  LocalAppData: {Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)}");
                sb.AppendLine($"  PATH entries: {(Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Length}");
            }
            catch (Exception ex)
            {
                sb.AppendLine($"  ERROR: {ex.Message}");
            }

            return sb.ToString();
        }

        private static void CheckFile(StringBuilder sb, string baseDir, string relative)
        {
            var full = Path.Combine(baseDir, relative);
            if (File.Exists(full))
            {
                var info = new FileInfo(full);
                sb.AppendLine($"  [OK]      {relative} ({info.Length} bytes)");
            }
            else
            {
                sb.AppendLine($"  [MISSING] {relative}");
            }
        }
    }
}
