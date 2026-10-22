using Microsoft.Extensions.Logging;

namespace VmpLauncher.App.Services;

public interface IActivationService
{
    /// <summary>Handle a vmpack:// link or .vmpack file path. Only ever navigates; never installs.</summary>
    void Handle(string activation);
}

public sealed class ActivationService(ILogger<ActivationService> log) : IActivationService
{
    public void Handle(string activation)
    {
        // Slice 7 wires this to the pack catalog + Home page. For now it is logged so the pipe path is testable.
        log.LogInformation("Activation received: {Activation}", activation);
    }
}
