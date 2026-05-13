using Summerdawn.Kagami.Persistence;

namespace Summerdawn.Kagami.Engine;

/// <summary>
/// Specifies how a <see cref="Link{TItem}"/> was established.
/// </summary>
public enum LinkKind
{
    /// <summary>
    /// Indicates the link is backed by a persisted <see cref="LinkStateRow"/>.
    /// </summary>
    Persisted,

    /// <summary>
    /// Indicates the link was inferred by similarity matching; no persisted link row exists yet.
    /// </summary>
    Inferred,

    /// <summary>
    /// Indicates the link has only one side — no matching item was found on the other side.
    /// </summary>
    Unmatched,

    /// <summary>
    /// Indicates matching was attempted but yielded multiple candidates, making automatic linking unsafe.
    /// </summary>
    Ambiguous,
}
