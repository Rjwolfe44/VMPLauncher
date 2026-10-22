using CKAN;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VmpLauncher.Ckan.Users;

/// <summary>
/// Headless CKAN <see cref="IUser"/>. Unlike CKAN's <c>NullUser</c> (which answers yes to everything and picks
/// option 0, which for the cache prompt means "delete the old cache") this one answers <b>no</b> to yes/no prompts,
/// picks the <i>last</i> option on selections (conventionally "cancel/revert"), and in strict mode throws so tests
/// catch any prompt we did not anticipate.
/// </summary>
public sealed class VmpCkanUser : IUser
{
    private readonly ILogger _log;
    private readonly bool _strict;
    private readonly IProgress<CkanProgress>? _progress;

    public VmpCkanUser(ILogger? log = null, bool strict = false, IProgress<CkanProgress>? progress = null)
    {
        _log = log ?? NullLogger.Instance;
        _strict = strict;
        _progress = progress;
    }

    public List<string> Errors { get; } = new();
    public List<string> Messages { get; } = new();

    public bool Headless => true;

    public bool RaiseYesNoDialog(string question)
    {
        _log.LogWarning("CKAN asked a yes/no question in headless mode (answering NO): {Question}", question);
        if (_strict)
        {
            throw new UnexpectedCkanPromptException(question);
        }
        return false;
    }

    public int RaiseSelectionDialog(string message, params object[] args)
    {
        _log.LogWarning("CKAN asked a selection question in headless mode: {Message} [{Count} options]", message, args.Length);
        if (_strict)
        {
            throw new UnexpectedCkanPromptException(message);
        }
        // Options may start with an int default index; the last real option is conventionally the "do nothing" one.
        return Math.Max(0, args.Length - 1);
    }

    public void RaiseError(string message, params object[] args)
    {
        var text = Format(message, args);
        Errors.Add(text);
        _log.LogError("CKAN: {Message}", text);
    }

    public void RaiseProgress(string message, int percent)
    {
        _progress?.Report(new CkanProgress(message, percent, null));
        _log.LogDebug("CKAN progress {Percent}%: {Message}", percent, message);
    }

    public void RaiseProgress(ByteRateCounter rateCounter)
    {
        _progress?.Report(new CkanProgress(rateCounter.Summary, rateCounter.Percent, rateCounter));
    }

    public void RaiseMessage(string message, params object[] args)
    {
        var text = Format(message, args);
        Messages.Add(text);
        _log.LogInformation("CKAN: {Message}", text);
    }

    private static string Format(string message, object[] args)
    {
        try
        {
            return args.Length == 0 ? message : string.Format(System.Globalization.CultureInfo.CurrentCulture, message, args);
        }
        catch (FormatException)
        {
            return message;
        }
    }
}

public sealed record CkanProgress(string Message, int Percent, ByteRateCounter? Rate);

public sealed class UnexpectedCkanPromptException(string prompt)
    : InvalidOperationException($"CKAN asked an unexpected question in headless mode: {prompt}")
{
    public string Prompt { get; } = prompt;
}
