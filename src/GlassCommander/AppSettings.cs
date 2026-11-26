using System.Text.Json;

namespace GlassCommander;

internal sealed class AppSettings
{
    public string? LeftPath { get; set; }
    public string? RightPath { get; set; }
    public List<string>? LeftTabs { get; set; }
    public List<string>? RightTabs { get; set; }
    public int LeftTabIndex { get; set; }
    public int RightTabIndex { get; set; }
    public bool ShowHidden { get; set; }
    public string Backdrop { get; set; } = "Mica";   // Mica | MicaAlt | Acrylic
    public int Width { get; set; } = 1400;
    public int Height { get; set; } = 860;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GlassCommander", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { /* sérült beállítás -> alapértelmezés */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
