namespace SpireReplay.SpireReplayCode;

/// <summary>Observe async completion without changing results, cancellation or game exceptions.</summary>
public static class TaskObservation
{
    public static async Task<T> Observe<T>(Task<T> original, Action<bool, T> completed)
    {
        bool success = false;
        T result = default!;
        try { result = await original; success = true; return result; }
        finally
        {
            try { completed(success, result); }
            catch { /* Observers never replace a game's result or exception. */ }
        }
    }

    public static async Task Observe(Task original, Action<bool> completed)
    {
        bool success = false;
        try { await original; success = true; }
        finally
        {
            try { completed(success); }
            catch { /* Observers never replace a game's result or exception. */ }
        }
    }
}
