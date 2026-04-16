
using System.Text.Json;

using Summerdawn.Kagami.Serialization;

namespace Summerdawn.Kagami.Authentication;
internal sealed class GoogleTokenCache
{
    private static readonly string StorageDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Summerdawn.ai", "Kagami", "tokens");

    public string? AccessToken { get; set; }

    public string? RefreshToken { get; set; }

    public DateTimeOffset Expiry { get; set; }

    public static GoogleTokenCache? Load(string endpointName)
    {
        string path = GetPath(endpointName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, KagamiJsonContext.Default.GoogleTokenCache);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string endpointName, GoogleTokenCache cache)
    {
        Directory.CreateDirectory(StorageDirectory);
        string path = GetPath(endpointName);
        string json = JsonSerializer.Serialize(cache, KagamiJsonContext.Default.GoogleTokenCache);
        File.WriteAllText(path, json);
    }

    private static string GetPath(string endpointName)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safeName = string.Concat(endpointName.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c));
        return Path.Combine(StorageDirectory, $"google_{safeName}.json");
    }
}
