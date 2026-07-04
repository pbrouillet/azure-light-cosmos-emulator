namespace Azure.Cosmos.LightEmulator.NoSql.Query;

/// <summary>
/// Bounds the number of query executions that may materialize a container in memory
/// concurrently.
/// </summary>
public interface IQueryExecutionLimiter
{
    /// <summary>
    /// Waits until a query-execution slot is available and returns a handle that releases
    /// the slot when disposed.
    /// </summary>
    ValueTask<IDisposable> AcquireAsync(CancellationToken ct);
}

/// <summary>
/// Default <see cref="IQueryExecutionLimiter"/> backed by a <see cref="SemaphoreSlim"/>.
/// <para>
/// The query engine fully materializes a container's documents (parsing every stored
/// document into a heavyweight <c>JsonNode</c> graph) on every query before paging. Under
/// high query concurrency the aggregate transient allocation grows with
/// <c>concurrency × containerSize</c> and can outrun the garbage collector, causing the
/// managed heap to balloon into many gigabytes (observed as an apparent "leak"/memory
/// saturation). Capping the number of simultaneous in-flight query executions keeps peak
/// memory bounded and the GC in a healthy, steady-state regime regardless of how many
/// clients issue queries in parallel.
/// </para>
/// </summary>
public sealed class QueryExecutionLimiter : IQueryExecutionLimiter, IDisposable
{
    /// <summary>Default concurrency: half the logical processors, at least two.</summary>
    public static int DefaultMaxConcurrency => Math.Max(2, Environment.ProcessorCount / 2);

    private readonly SemaphoreSlim _gate;

    public QueryExecutionLimiter(int maxConcurrency)
    {
        _gate = new SemaphoreSlim(Math.Max(1, maxConcurrency));
    }

    public QueryExecutionLimiter() : this(DefaultMaxConcurrency)
    {
    }

    public async ValueTask<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(_gate);
    }

    public void Dispose() => _gate.Dispose();

    private sealed class Releaser : IDisposable
    {
        private SemaphoreSlim? _gate;

        public Releaser(SemaphoreSlim gate) => _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
