using System.IO;
using VmpLauncher.Core;
using VmpLauncher.Core.Serialization;

namespace VmpLauncher.App.Services;

/// <summary>Per-profile play statistics kept in playtime.json (seconds, sessions, last played UTC).</summary>
public sealed record PlayStats
{
    public long Seconds { get; init; }
    public int Sessions { get; init; }
    public DateTime? LastPlayedUtc { get; init; }
}

public sealed record PlaytimeFile
{
    public PlayStats Total { get; init; } = new();
    public Dictionary<string, PlayStats> Profiles { get; init; } = new();
}

/// <summary>
/// Counts the time KSP runs when the launcher started it. Every flush adds the time since the previous flush to the
/// file, so a launcher that dies mid-session keeps everything up to its last flush (about a minute). Nothing is sent anywhere.
/// </summary>
public sealed class PlaytimeCounter
{
    /// <summary>One flush never adds more than this (sleep, hibernation or a clock jump must not count as play).</summary>
    public static readonly TimeSpan MaxStep = TimeSpan.FromMinutes(5);
    public const long MaxSeconds = 100_000L * 3600;
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(60);

    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private PlaytimeFile _data;

    public PlaytimeCounter(LauncherPaths paths, TimeProvider? clock = null)
    {
        _path = Path.Combine(paths.DataRoot, "playtime.json");
        _clock = clock ?? TimeProvider.System;
        _data = Load();
    }

    public PlayStats Total { get { lock (_gate) { return _data.Total; } } }

    public PlayStats Profile(string profileId)
    {
        lock (_gate) { return _data.Profiles.TryGetValue(profileId, out var s) ? s : new PlayStats(); }
    }

    /// <summary>Starts a session (counted right away) and returns the handle that adds elapsed time on each Flush.</summary>
    public Session Begin(string profileId)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        Add(profileId, 0, 1, now);
        return new Session(this, profileId, now);
    }

    /// <summary>Runs one session until <paramref name="gameExited"/> completes (or cancel), flushing every minute. The only timer, and only while a game runs.</summary>
    public async Task TrackAsync(string profileId, Task gameExited, CancellationToken ct)
    {
        var session = Begin(profileId);
        try
        {
            using var timer = new PeriodicTimer(FlushInterval, _clock);
            while (true)
            {
                var tick = timer.WaitForNextTickAsync(ct).AsTask();
                if (await Task.WhenAny(gameExited, tick).ConfigureAwait(false) == gameExited)
                {
                    break;
                }
                await tick.ConfigureAwait(false);
                session.Flush();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            session.Flush();
        }
    }

    public sealed class Session
    {
        private readonly PlaytimeCounter _owner;
        private readonly string _profile;
        private DateTime _last;

        internal Session(PlaytimeCounter owner, string profile, DateTime start)
        {
            _owner = owner;
            _profile = profile;
            _last = start;
        }

        public void Flush()
        {
            var now = _owner._clock.GetUtcNow().UtcDateTime;
            var delta = now - _last;
            _last = now;
            if (delta < TimeSpan.Zero)
            {
                delta = TimeSpan.Zero;
            }
            if (delta > MaxStep)
            {
                delta = MaxStep;
            }
            _owner.Add(_profile, (long)delta.TotalSeconds, 0, now);
        }
    }

    private void Add(string profileId, long seconds, int sessions, DateTime nowUtc)
    {
        lock (_gate)
        {
            PlayStats Bump(PlayStats s) => s with
            {
                Seconds = Math.Clamp(s.Seconds + seconds, 0, MaxSeconds),
                Sessions = Math.Clamp(s.Sessions + sessions, 0, int.MaxValue),
                LastPlayedUtc = nowUtc,
            };
            var profiles = new Dictionary<string, PlayStats>(_data.Profiles)
            {
                [profileId] = Bump(_data.Profiles.GetValueOrDefault(profileId) ?? new PlayStats()),
            };
            _data = new PlaytimeFile { Total = Bump(_data.Total), Profiles = profiles };
            try
            {
                LauncherJson.WriteFile(_path, _data);
            }
            catch (IOException)
            {
                // Stays counted in memory; the next flush writes it.
            }
        }
    }

    private PlaytimeFile Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new PlaytimeFile();
            }
            var f = LauncherJson.ReadFile<PlaytimeFile>(_path);
            PlayStats Fix(PlayStats s) => s with { Seconds = Math.Clamp(s.Seconds, 0, MaxSeconds), Sessions = Math.Max(0, s.Sessions) };
            return new PlaytimeFile
            {
                Total = Fix(f.Total ?? new PlayStats()),
                Profiles = (f.Profiles ?? new()).ToDictionary(p => p.Key, p => Fix(p.Value ?? new PlayStats())),
            };
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            return new PlaytimeFile();
        }
    }
}
