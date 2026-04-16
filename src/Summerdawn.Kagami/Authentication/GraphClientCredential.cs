namespace Summerdawn.Kagami.Authentication;

using Azure.Core;

/// <summary>
/// Resolved Microsoft Graph credential wrapping an Azure <see cref="TokenCredential"/>.
/// </summary>
public sealed class GraphClientCredential(TokenCredential tokenCredential) : IConnectorCredential
{
    public TokenCredential TokenCredential { get; } = tokenCredential;
}
