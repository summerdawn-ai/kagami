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
        private readonly SemaphoreSlim gate = new(1, 1);
        private readonly Func<CancellationToken, Task> loader = loader;
        private bool loaded;

        public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
        {
            if (loaded)
            {
                return;
            }

            await gate.WaitAsync(cancellationToken);
            try
            {
                if (loaded)
                {
                    return;
                }

                await loader(cancellationToken);
                loaded = true;
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
