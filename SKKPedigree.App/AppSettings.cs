using System;
using System.IO;
using System.Text.Json;

namespace SKKPedigree.App
{
    public class AppSettings
    {
        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SKKPedigree", "settings.json");

        public string DatabasePath { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SKKPedigree", "pedigree.db");

        public int DefaultGenerations { get; set; } = 4;
        public int RequestDelayMs { get; set; } = 1500;
        public bool HeadlessBrowser { get; set; } = true;
        public int MaxConcurrentRequests { get; set; } = 1;

        public static AppSettings Load()
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            try
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            catch
            {
                return new AppSettings();
            }
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
    }
}
