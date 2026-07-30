using Azure.Core;

namespace Summerdawn.Kagami.Authentication;

/// <summary>
/// Resolved Microsoft client credential wrapping an Azure <see cref="TokenCredential"/>.
/// </summary>
public sealed class MicrosoftClientCredential(TokenCredential tokenCredential) : IConnectorCredential
{
    /// <summary>
    /// Gets the Azure credential used to acquire access tokens.
    /// </summary>
    public TokenCredential TokenCredential { get; } = tokenCredential;
}
