using System.Collections.Concurrent;

namespace ContiExif.Services;

public enum ThumbState { Idle, Running, Paused }

/// <summary>Builds missing thumbnails in the background (newest photos first) with two low-priority workers.</summary>
public sealed class ThumbBuilder
{
    private const int Workers = 2;

    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private ManualResetEventSlim _gate = new(true);
    private int _run;
    private int _done, _total;
    private bool _active, _paused;

    public event Action<int, int, ThumbState>? Changed;

    public void Start(Database db, string owner)
    {
        Stop();
        int run;
        CancellationTokenSource cts;
        ManualResetEventSlim gate;
        lock (_lock)
        {
            run = ++_run;
            cts = _cts = new CancellationTokenSource();
            gate = _gate = new ManualResetEventSlim(true);
            _paused = false;
            _active = false;
        }
        var t = new Thread(() => Run(db, owner, run, cts.Token, gate))
        {
            IsBackground = true,
            Name = "ThumbBuilder",
            Priority = ThreadPriority.BelowNormal
        };
        t.Start();
    }

    public void Stop()
    {
        lock (_lock)
        {
            _cts?.Cancel();
            _gate.Set();
            _run++;
            _active = false;
        }
    }

    public void TogglePause()
    {
        int d, t;
        ThumbState st;
        lock (_lock)
        {
            if (!_active) return;
            _paused = !_paused;
            if (_paused) _gate.Reset(); else _gate.Set();
            d = _done; t = _total;
            st = _paused ? ThumbState.Paused : ThumbState.Running;
        }
        Changed?.Invoke(d, t, st);
    }

    private void Report(int run, int done, int total, ThumbState state)
    {
        if (run != Volatile.Read(ref _run)) return;
        Changed?.Invoke(done, total, state);
    }

    private void Run(Database db, string owner, int run, CancellationToken ct, ManualResetEventSlim gate)
    {
        try
        {
            var store = ThumbStore.Instance;
            var photos = db.LoadThumbWork();
            var have = store.KeysFor(owner);
            var pending = new ConcurrentQueue<(string Key, string Path)>();
            int done = 0;
            foreach (var p in photos)
            {
                string k = ThumbnailService.Key(owner, p.Path, p.MTime, p.Size);
                if (have.Contains(k)) done++;
                else pending.Enqueue((k, p.Path));
            }
            int total = photos.Count;
            if (pending.IsEmpty) return;

            lock (_lock) { _done = done; _total = total; _active = true; }
            Report(run, done, total, ThumbState.Running);

            var buffer = new List<(string, string, byte[])>();
            var bufLock = new object();

            void Flush()
            {
                List<(string, string, byte[])> batch;
                lock (bufLock)
                {
                    if (buffer.Count == 0) return;
                    batch = new List<(string, string, byte[])>(buffer);
                    buffer.Clear();
                }
                store.PutMany(batch);
            }

            void Tick()
            {
                int d = Interlocked.Increment(ref done);
                _done = d;
                if (d % 25 == 0) Report(run, d, total, _paused ? ThumbState.Paused : ThumbState.Running);
            }

            void Worker()
            {
                try
                {
                    while (!ct.IsCancellationRequested && pending.TryDequeue(out var it))
                    {
                        gate.Wait(ct);
                        if (store.Has(it.Key)) { Tick(); continue; }
                        var bytes = ThumbnailService.Generate(it.Path);
                        if (bytes != null)
                        {
                            bool flush;
                            lock (bufLock)
                            {
                                buffer.Add((it.Key, owner, bytes));
                                flush = buffer.Count >= 40;
                            }
                            if (flush) Flush();
                        }
                        Tick();
                    }
                }
                catch (OperationCanceledException) { }
                catch { }
            }

            var threads = Enumerable.Range(0, Workers).Select(_ =>
            {
                var t = new Thread(Worker) { IsBackground = true, Priority = ThreadPriority.BelowNormal };
                t.Start();
                return t;
            }).ToList();
            foreach (var t in threads) t.Join();
            Flush();

            if (!ct.IsCancellationRequested)
            {
                lock (_lock) { _active = false; }
                Report(run, done, total, ThumbState.Idle);
            }
        }
        catch { }
    }
}
