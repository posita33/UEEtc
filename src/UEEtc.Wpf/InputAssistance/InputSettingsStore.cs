using System.IO;
using System.Text;
using System.Text.Json;

namespace UEEtc.Wpf.InputAssistance;

// UI preferences only. No source, generated plan, selected candidates or apply state are persisted.
public sealed class InputSettingsStore
{
    public static InputSettingsStore Default { get; } = new(System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UEEtc", "input-settings.json"));
    private readonly object gate = new();
    private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);
    private bool dirty;
    public string Path { get; }
    public string? Error { get; private set; }

    public InputSettingsStore(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        try
        {
            if (!File.Exists(Path)) return;
            var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path));
            if (loaded is null || loaded.Any(p => p.Value is null)) throw new JsonException("入力設定の形式が不正です。");
            foreach (var pair in loaded) values[pair.Key] = pair.Value;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { Error = "入力設定を読み込めませんでした: " + error.Message; }
    }

    public string? Get(string key) { lock (gate) return values.GetValueOrDefault(key); }
    public void Set(string key, string value)
    {
        lock (gate)
        {
            if (values.TryGetValue(key, out var current) && current == value) return;
            values[key] = value; dirty = true;
        }
    }
    public bool Flush()
    {
        lock (gate)
        {
            if (!dirty) return Error is null;
            string temp = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.WriteAllText(temp, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
                File.Move(temp, Path, overwrite: true);
                dirty = false; Error = null;
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Error = "入力設定を保存できませんでした: " + error.Message; return false; }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
