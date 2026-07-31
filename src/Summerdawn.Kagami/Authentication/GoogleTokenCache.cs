using System.Text.Json;

using Summerdawn.Kagami.Serialization;

namespace Summerdawn.Kagami.Authentication;

/// <summary>
/// Persists Google OAuth tokens beneath a configured Kagami data directory.
/// </summary>
public sealed class GoogleTokenCache(string storageDirectory)
{
    private readonly string storageDirectory = Path.Combine(storageDirectory, "tokens");

    /// <summary>
    /// Loads the cached tokens for an endpoint.
    /// </summary>
    public GoogleTokenCacheEntry? Load(string endpointName)
    {
        string path = GetPath(endpointName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize(json, KagamiJsonContext.Default.GoogleTokenCacheEntry);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Saves the tokens for an endpoint.
    /// </summary>
    public void Save(string endpointName, GoogleTokenCacheEntry cacheEntry)
    {
        Directory.CreateDirectory(storageDirectory);
        string path = GetPath(endpointName);
        string json = JsonSerializer.Serialize(cacheEntry, KagamiJsonContext.Default.GoogleTokenCacheEntry);
        File.WriteAllText(path, json);
    }

    private string GetPath(string endpointName)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safeName = string.Concat(endpointName.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c));
        return Path.Combine(storageDirectory, $"google_{safeName}.json");
    }
}

/// <summary>
/// Represents the tokens and expiry returned by Google OAuth.
/// </summary>
public sealed record GoogleTokenCacheEntry(
    string? AccessToken,
    string? RefreshToken,
    DateTimeOffset Expiry,
    string? AccountEmail = null,
    string? IdToken = null);
