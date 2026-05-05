using System.Runtime.CompilerServices;

using Summerdawn.Kagami.Models;

namespace Summerdawn.Kagami.Connectors;

/// <summary>
/// Manages lazy-loaded contact photos. Connectors attach a loader delegate to a contact
/// via <see cref="Attach"/>; the executor calls <see cref="EnsureLoadedAsync"/> before write
/// operations to ensure photo data is available.
/// </summary>
internal static class ContactPhotoLoader
{
    private static readonly ConditionalWeakTable<CanonicalContact, LoaderState> Loaders = [];

    /// <summary>
    /// Attaches a lazy photo loader to <paramref name="item"/>, replacing any previously attached loader.
    /// </summary>
    public static void Attach(CanonicalContact item, Func<CancellationToken, Task> loader)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(loader);

        Loaders.Remove(item);
        Loaders.Add(item, new LoaderState(loader));
    }

    /// <summary>
    /// Ensures the photo for <paramref name="item"/> has been loaded. When no loader is attached,
    /// the call is a no-op.
    /// </summary>
    public static async Task EnsureLoadedAsync(CanonicalContact item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!Loaders.TryGetValue(item, out var state))
        {
            return;
        }

        await state.EnsureLoadedAsync(cancellationToken);
    }

    /// <summary>
    /// Tracks the loading state for a single contact's photo, ensuring the loader is invoked
    /// at most once even under concurrent callers.
    /// </summary>
    private sealed class LoaderState(Func<CancellationToken, Task> loader)
    {
        private readonly Lock gate = new();
        private Task? loadingTask;
        private bool loaded;

        /// <summary>
        /// Ensures the loader has run exactly once. Concurrent callers share the same underlying
        /// task; the loader is called with <see cref="CancellationToken.None"/> so that a
        /// cancellation from one caller does not abort the shared load for others.
        /// </summary>
        public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Task? pendingTask;
            lock (gate)
            {
                if (loaded)
                {
                    return;
                }

                pendingTask = loadingTask;
                if (pendingTask is null)
                {
                    pendingTask = loader(CancellationToken.None);
                    loadingTask = pendingTask;
                }
            }

            try
            {
                await pendingTask.WaitAsync(cancellationToken);
            }
            finally
            {
                lock (gate)
                {
                    if (pendingTask.IsCompletedSuccessfully)
                    {
                        loaded = true;
                    }

                    if (!loaded && ReferenceEquals(loadingTask, pendingTask))
                    {
                        loadingTask = null;
                    }
                }
            }
        }
    }
}
