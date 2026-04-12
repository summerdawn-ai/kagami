namespace Summerdawn.Kagami.Connectors;

using System.Runtime.CompilerServices;

using Summerdawn.Kagami.Models;

internal static class ContactPhotoLoader
{
    private static readonly ConditionalWeakTable<CanonicalItem, LoaderState> Loaders = new();

    public static void Attach(CanonicalItem item, Func<CancellationToken, Task> loader)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(loader);

        Loaders.Remove(item);
        Loaders.Add(item, new LoaderState(loader));
    }

    public static async Task EnsureLoadedAsync(CanonicalItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!Loaders.TryGetValue(item, out LoaderState? state))
        {
            return;
        }

        await state.EnsureLoadedAsync(cancellationToken);
    }

    private sealed class LoaderState(Func<CancellationToken, Task> loader)
    {
        private readonly object gate = new();
        private readonly Func<CancellationToken, Task> loader = loader;
        private Task? loadingTask;
        private bool loaded;

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
