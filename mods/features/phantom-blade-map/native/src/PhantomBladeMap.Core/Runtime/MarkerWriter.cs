using System.Text;

namespace PhantomBladeMap.Core.Runtime;

// One writer and one replaceable pending snapshot. A slower disk never grows a
// queue of obsolete saves, and a world transition never changes the target file.
internal sealed class MarkerWriter(string path)
{
    private readonly object gate = new();
    private Func<string>? pending;
    private bool running;
    private bool closing;
    private Task completion = Task.CompletedTask;
    private string? error;
    public string? Error => Volatile.Read(ref error);

    public void Save(string snapshot) => Save(() => snapshot);

    public void Save(Func<string> snapshot)
    {
        lock (gate)
        {
            if (closing) throw new ObjectDisposedException(nameof(MarkerWriter));
            pending = snapshot;
            if (running) return;
            running = true;
            completion = Task.Run(WritePendingAsync);
        }
    }

    public Task CompleteAsync()
    {
        lock (gate)
        {
            closing = true;
            return completion;
        }
    }

    private async Task WritePendingAsync()
    {
        while (true)
        {
            Func<string> snapshot;
            lock (gate)
            {
                if (pending is null) { running = false; return; }
                snapshot = pending;
                pending = null;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + ".pending";
                await File.WriteAllTextAsync(temporary, snapshot(), Encoding.UTF8).ConfigureAwait(false);
                File.Move(temporary, path, overwrite: true);
                Volatile.Write(ref error, null);
            }
            catch (Exception failure)
            {
                Volatile.Write(ref error, failure.GetType().Name);
            }
        }
    }
}
