using System.Management.Automation.Runspaces;
using System.Threading.Channels;

namespace PowerCode.PsMcp;

/// <summary>
/// A lightweight pool of individually-created <see cref="Runspace"/> instances.
/// Each runspace is initialized by a caller-supplied factory, ensuring identical
/// configuration (modules, dot-sourced scripts, <c>$PSScriptRoot</c>, etc.).
/// </summary>
/// <remarks>
/// Unlike <see cref="RunspacePool"/>, which uses <see cref="InitialSessionState"/>
/// and <see cref="SessionStateFunctionEntry"/> (losing source-file associations and
/// <c>$PSScriptRoot</c>), this pool creates fully independent runspaces through the
/// same code path as the discovery runspace.
/// </remarks>
internal sealed class ManagedRunspacePool : IDisposable
{
    private readonly Channel<Runspace> _channel;
    private readonly Runspace[] _runspaces;

    /// <summary>
    /// Creates and opens <paramref name="size"/> runspaces using <paramref name="factory"/>.
    /// </summary>
    /// <param name="size">Number of runspaces in the pool.</param>
    /// <param name="factory">
    ///   Factory delegate called <paramref name="size"/> times to produce configured,
    ///   opened runspaces (modules imported, scripts dot-sourced, etc.).
    /// </param>
    public ManagedRunspacePool(int size, Func<Runspace> factory)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentNullException.ThrowIfNull(factory);

        _channel = Channel.CreateBounded<Runspace>(size);
        _runspaces = new Runspace[size];

        for (var i = 0; i < size; i++)
        {
            var rs = factory();
            _runspaces[i] = rs;
            _channel.Writer.TryWrite(rs);
        }
    }

    /// <summary>
    /// Rents a runspace from the pool, waiting asynchronously if all are in use.
    /// </summary>
    public ValueTask<Runspace> RentAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAsync(cancellationToken);

    /// <summary>
    /// Returns a runspace to the pool after use.
    /// </summary>
    public void Return(Runspace runspace)
        => _channel.Writer.TryWrite(runspace);

    /// <summary>
    /// Disposes all runspaces in the pool.
    /// </summary>
    public void Dispose()
    {
        foreach (var rs in _runspaces)
            rs.Dispose();
    }
}
