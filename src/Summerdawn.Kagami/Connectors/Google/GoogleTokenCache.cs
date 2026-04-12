namespace Summerdawn.Kagami.Connectors.Google;

using System.Text.Json;

internal sealed class GoogleTokenCache
{
    private static readonly string StorageDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Summerdawn.ai", "Kagami", "tokens");

    public string? AccessToken { get; set; }

    public string? RefreshToken { get; set; }

    public DateTimeOffset Expiry { get; set; }

    public static GoogleTokenCache? Load(string userLogin)
    {
        string path = GetPath(userLogin);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<GoogleTokenCache>(json);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string userLogin, GoogleTokenCache cache)
    {
        Directory.CreateDirectory(StorageDirectory);
        string path = GetPath(userLogin);
        string json = JsonSerializer.Serialize(cache);
        File.WriteAllText(path, json);
    }

    private static string GetPath(string userLogin)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safeName = string.Concat(userLogin.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c));
        return Path.Combine(StorageDirectory, $"google_{safeName}.json");
    }
}
