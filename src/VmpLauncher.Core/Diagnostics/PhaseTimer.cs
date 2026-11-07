using System.Diagnostics;
using System.Globalization;

namespace VmpLauncher.Core.Diagnostics;

/// <summary>
/// Wall-clock time per phase of a long operation, written as one line per phase
/// (<c>Update phase snapshot: 4.2 s (812 files)</c>) and one summary line, so every log shows where the time went.
/// Costs one Stopwatch read per phase.
/// </summary>
public sealed class PhaseTimer
{
    private readonly string _operation;
    private readonly Action<string> _write;
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly List<(string Name, TimeSpan Elapsed)> _phases = new();

    public PhaseTimer(string operation, Action<string> write)
    {
        _operation = operation.Length == 0 ? operation : char.ToUpperInvariant(operation[0]) + operation[1..];
        _write = write;
    }

    public IReadOnlyList<(string Name, TimeSpan Elapsed)> Phases => _phases;

    /// <summary>Starts a phase; disposing the handle ends it. <see cref="Phase.Detail"/> is appended to the line.</summary>
    public Phase Start(string name) => new(this, name);

    public T Time<T>(string name, Func<T> work, Func<T, string?>? detail = null)
    {
        using var p = Start(name);
        var result = work();
        p.Detail = detail?.Invoke(result);
        return result;
    }

    public void Time(string name, Action work)
    {
        using var _ = Start(name);
        work();
    }

    /// <summary>One line with every phase, the total and the time outside the timed phases.</summary>
    public string Summary(string outcome)
    {
        var total = _total.Elapsed;
        var timed = TimeSpan.FromTicks(_phases.Sum(p => p.Elapsed.Ticks));
        var parts = _phases.Select(p => $"{p.Name} {Format(p.Elapsed)}");
        return $"{_operation} phases ({outcome}): {string.Join(", ", parts)}; other {Format(total - timed)}; total {Format(total)}";
    }

    public void WriteSummary(string outcome) => _write(Summary(outcome));

    public static string Format(TimeSpan t) => t.TotalSeconds.ToString(t.TotalSeconds < 10 ? "0.00" : "0.0", CultureInfo.InvariantCulture) + " s";

    public sealed class Phase : IDisposable
    {
        private readonly PhaseTimer _owner;
        private readonly string _name;
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private bool _done;

        internal Phase(PhaseTimer owner, string name)
        {
            _owner = owner;
            _name = name;
        }

        public string? Detail { get; set; }

        public void Dispose()
        {
            if (_done)
            {
                return;
            }
            _done = true;
            var elapsed = _sw.Elapsed;
            _owner._phases.Add((_name, elapsed));
            _owner._write($"{_owner._operation} phase {_name}: {Format(elapsed)}{(Detail is null ? string.Empty : $" ({Detail})")}");
        }
    }
}
