using Microsoft.Extensions.Primitives;

namespace Docuconf;

/// <summary>
/// A file input whose handle reloads it (<see cref="ConfigFile{T}"/>, <see cref="TlsKeyPair"/>, <see cref="Keystore"/>):
/// a changed file replaces the value only after it passes the startup checks (SPEC §4.6.2).
/// </summary>
public interface IWatchedInput
{
    /// <summary>The input's name in the contract; empty when docuconf did not load it.</summary>
    string Input { get; }

    /// <summary>The reload status, for a health check or a metric. It never holds file content.</summary>
    ReloadStatus Status { get; }

    /// <summary>
    /// A change token that fires once the next accepted reload has replaced the value, for
    /// <see cref="ChangeToken.OnChange(Func{IChangeToken?}, Action)"/>. A rejected change never fires it.
    /// </summary>
    IChangeToken GetReloadToken();
}

/// <summary>The reload status of a watched input.</summary>
/// <param name="Generation">1 after startup, plus one per accepted reload; 0 when the input was not loaded.</param>
/// <param name="LastReload">When the last accepted reload replaced the value; null until one has.</param>
/// <param name="LastRejection">The last rejected change, cleared by a later accepted one.</param>
public sealed record ReloadStatus(long Generation, DateTimeOffset? LastReload, ReloadRejection? LastRejection);

/// <summary>A changed file that failed the startup checks, so the previous value stayed. It holds no content.</summary>
/// <param name="At">When the change was rejected.</param>
/// <param name="Input">The input's name.</param>
/// <param name="Codes">The violation codes (SPEC §11.2), such as <c>schema_mismatch</c> or <c>keystore_unreadable</c>.</param>
public sealed record ReloadRejection(DateTimeOffset At, string Input, IReadOnlyList<string> Codes);

/// <summary>
/// Reloads one file input: when its files' timestamps change, at most once per interval, on the read that notices
/// it, or from a timer while callbacks are registered. A new value replaces the old one only when it passes the
/// checks; callbacks then run with it, outside the lock, one after another.
/// </summary>
internal sealed class Watcher<T>
    where T : class
{
    private readonly object _gate = new();
    private readonly object _fire = new();
    private readonly string[] _paths;
    private readonly TimeSpan _interval;
    private readonly Func<List<Violation>, T?> _load;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TextWriter _log;
    private Func<T>? _initial;
    private T? _value;
    private long _generation = 1;
    private DateTimeOffset? _lastReload;
    private ReloadRejection? _lastRejection;
    private DateTime[] _stamp;
    private DateTimeOffset _nextCheck;
    private List<Action<T>> _callbacks = [];
    private Timer? _timer;
    private CancellationTokenSource _token = new();

    /// <param name="input">The input's name, for the status and the log.</param>
    /// <param name="paths">The files whose timestamps say the input changed.</param>
    /// <param name="interval">How often the files are looked at, at most.</param>
    /// <param name="initial">The value read at startup, made on first use.</param>
    /// <param name="load">Loads and checks the files; null with the violations when they fail.</param>
    /// <param name="clock">The current time.</param>
    /// <param name="log">Where a rejected change and a failing callback are reported, by name and code only.</param>
    public Watcher(string input, string[] paths, TimeSpan interval, Func<T> initial, Func<List<Violation>, T?> load, Func<DateTimeOffset> clock, TextWriter log)
    {
        Input = input;
        _paths = paths;
        _interval = interval;
        _initial = initial;
        _load = load;
        _clock = clock;
        _log = log;
        _stamp = Stamps(paths);
        _nextCheck = clock() + interval;
    }

    public string Input { get; }

    /// <summary>The current value, after looking for a change if the interval has passed.</summary>
    public T Current
    {
        get
        {
            Poll(force: false);
            lock (_gate)
            {
                return Value();
            }
        }
    }

    public ReloadStatus Status
    {
        get
        {
            lock (_gate)
            {
                return new ReloadStatus(_generation, _lastReload, _lastRejection);
            }
        }
    }

    public IChangeToken GetReloadToken()
    {
        lock (_gate)
        {
            return new CancellationChangeToken(_token.Token);
        }
    }

    /// <summary>Registers a callback; while any is registered, a timer looks for changes without a read.</summary>
    public IDisposable OnChange(Action<T> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_gate)
        {
            _callbacks = [.. _callbacks, callback];
            _timer ??= new Timer(_ => Tick(), null, _interval, _interval);
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                var rest = new List<Action<T>>(_callbacks);
                rest.Remove(callback);
                _callbacks = rest;
                if (rest.Count == 0)
                {
                    _timer?.Dispose();
                    _timer = null;
                }
            }
        });
    }

    private void Tick()
    {
        try
        {
            Poll(force: true);
        }
        catch (Exception ex)
        {
            // A timer callback must not throw; Poll itself only fails on a broken log writer.
            Log($"docuconf: warning: {Input}: looking for a reload failed with {ex.GetType().FullName}");
        }
    }

    /// <summary>Looks for a change now when <paramref name="force"/>, else only once the interval has passed.</summary>
    internal void Poll(bool force)
    {
        T accepted;
        List<Action<T>> callbacks;
        CancellationTokenSource fired;
        lock (_gate)
        {
            var now = _clock();
            if (!force && now < _nextCheck)
            {
                return;
            }

            _nextCheck = now + _interval;
            // Kubernetes swaps a ..data symlink, so compare the files' own timestamps.
            var stamp = Stamps(_paths);
            if (stamp.AsSpan().SequenceEqual(_stamp))
            {
                return;
            }

            // A rejected change is not retried until the files change again, so it is reported once.
            _stamp = stamp;
            var problems = new List<Violation>();
            T? loaded;
            try
            {
                loaded = _load(problems);
            }
            catch (Exception ex)
            {
                // Never the exception's message: it may quote the file.
                loaded = null;
                problems.Add(new Violation(Codes.FileUnreadable, Input, ex.GetType().Name));
            }

            if (loaded is null)
            {
                string[] codes = problems.Count == 0 ? [Codes.FileMissing] : problems.Select(p => p.Code).Distinct(StringComparer.Ordinal).ToArray();
                _lastRejection = new ReloadRejection(now, Input, codes);
                Log($"docuconf: warning: {Input}: a changed file was rejected ({string.Join(", ", codes)}); keeping the previous value");
                return;
            }

            _value = loaded;
            _initial = null;
            _generation++;
            _lastReload = now;
            _lastRejection = null;
            accepted = loaded;
            callbacks = _callbacks;
            fired = _token;
            _token = new CancellationTokenSource();
        }

        lock (_fire)
        {
            foreach (var callback in callbacks)
            {
                try
                {
                    callback(accepted);
                }
                catch (Exception ex)
                {
                    // By name and type only: the message could hold the value.
                    Log($"docuconf: warning: {Input}: an OnChange callback threw {ex.GetType().FullName}");
                }
            }

            try
            {
                fired.Cancel();
            }
            catch (AggregateException ex)
            {
                Log($"docuconf: warning: {Input}: a reload token callback threw {ex.InnerException?.GetType().FullName ?? ex.GetType().FullName}");
            }
        }
    }

    private T Value()
    {
        if (_initial is { } initial)
        {
            _value = initial();
            _initial = null;
        }

        return _value!;
    }

    private void Log(string line)
    {
        try
        {
            _log.WriteLine(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private static DateTime[] Stamps(string[] paths) => paths.Select(Stamp).ToArray();

    private static DateTime Stamp(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}

/// <summary>What an input that docuconf did not load returns.</summary>
internal static class Unwatched
{
    public static readonly ReloadStatus Status = new(0, null, null);

    public static readonly IDisposable Subscription = new Nothing();

    /// <summary>A token that never fires.</summary>
    public static readonly IChangeToken Token = new CancellationChangeToken(CancellationToken.None);

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
