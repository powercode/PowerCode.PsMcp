using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Security;
using ModelContextProtocol;

namespace PowerCode.PsMcp;

/// <summary>
/// Custom <see cref="PSHostUserInterface"/> that routes all PowerShell output streams to
/// stderr, preserving stdout for MCP JSON-RPC communication. Interactive operations
/// (prompts, credentials, secure string input) throw <see cref="NotSupportedException"/>
/// because no interactive user is present in MCP server mode.
/// Transport-facing stderr output is intentionally separate from client-visible MCP log
/// notifications emitted during a tool invocation.
/// </summary>
internal sealed class McpPsHostUserInterface : PSHostUserInterface
{
    private readonly McpPsHostRawUserInterface _rawUi = new();

    /// <summary>
    /// Per-invocation progress reporter. When set, <see cref="WriteProgress"/> forwards
    /// <see cref="ProgressRecord"/> updates and <see cref="WriteWarningLine"/>,
    /// <see cref="WriteVerboseLine"/>, and <see cref="WriteDebugLine"/> send message-only
    /// notifications (Progress = 0, Total = null) to the MCP client instead of writing to stderr.
    /// Must only be mutated while the runspace lock is held.
    /// </summary>
    internal IProgress<ProgressNotificationValue>? Progress { get; set; }

    /// <inheritdoc />
    public override PSHostRawUserInterface RawUI => _rawUi;

    // ── Reads ────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>Returns an empty string; interactive input is not supported in MCP mode.</remarks>
    public override string ReadLine() => string.Empty;

    /// <inheritdoc />
    public override SecureString ReadLineAsSecureString() =>
        throw new NotSupportedException("Interactive input is not supported in MCP server mode.");

    // ── Writes ───────────────────────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>Writes to stderr to avoid corrupting the JSON-RPC stream on stdout.</remarks>
    public override void Write(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value) =>
        Console.Error.Write(value);

    /// <inheritdoc />
    public override void Write(string value) => Console.Error.Write(value);

    /// <inheritdoc />
    public override void WriteLine(string value) => Console.Error.WriteLine(value);

    /// <inheritdoc />
    public override void WriteLine() => Console.Error.WriteLine();

    /// <inheritdoc />
    public override void WriteErrorLine(string value) => Console.Error.WriteLine($"ERROR: {value}");

    /// <inheritdoc />
    public override void WriteWarningLine(string message)
    {
        Console.Error.WriteLine($"WARNING: {message}");
        Progress?.Report(new ProgressNotificationValue { Progress = 0f, Message = $"WARNING: {message}" });
    }

    /// <inheritdoc />
    public override void WriteVerboseLine(string message)
    {
        Console.Error.WriteLine($"VERBOSE: {message}");
        Progress?.Report(new ProgressNotificationValue { Progress = 0f, Message = $"VERBOSE: {message}" });
    }

    /// <inheritdoc />
    public override void WriteDebugLine(string message)
    {
        Console.Error.WriteLine($"DEBUG: {message}");
        Progress?.Report(new ProgressNotificationValue { Progress = 0f, Message = $"DEBUG: {message}" });
    }

    /// <inheritdoc />
    public override void WriteProgress(long sourceId, ProgressRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (Progress is { } reporter)
        {
            // Completed/Stopped records signal the end of an activity; skip them to
            // avoid confusing clients with a 100 % notification after the real ones.
            if (record.RecordType is ProgressRecordType.Completed)
                return;

            var message = string.IsNullOrEmpty(record.CurrentOperation)
                ? $"{record.Activity}: {record.StatusDescription}"
                : $"{record.Activity}: {record.StatusDescription} — {record.CurrentOperation}";

            reporter.Report(new ProgressNotificationValue
            {
                // PercentComplete is -1 when unknown; clamp to 0 in that case.
                Progress = record.PercentComplete >= 0 ? record.PercentComplete : 0f,
                Total = 100f,
                Message = message
            });
        }
        else
        {
            Console.Error.WriteLine(
                $"PROGRESS [{record.ActivityId}]: {record.Activity} – {record.StatusDescription}");
        }
    }

    /// <inheritdoc />
    public override void WriteInformation(InformationRecord record)
    {
        if (record is not null)
        {
            var text = record.MessageData?.ToString() ?? string.Empty;
            Console.Error.WriteLine($"INFO: {text}");
        }
    }

    // ── Prompts ──────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public override Dictionary<string, PSObject> Prompt(
        string caption,
        string message,
        Collection<FieldDescription> descriptions) =>
        throw new NotSupportedException("Interactive prompts are not supported in MCP server mode.");

    /// <inheritdoc />
    /// <remarks>Returns <paramref name="defaultChoice"/> immediately; no user interaction.</remarks>
    public override int PromptForChoice(
        string caption,
        string message,
        Collection<ChoiceDescription> choices,
        int defaultChoice) =>
        defaultChoice;

    /// <inheritdoc />
    public override PSCredential PromptForCredential(
        string caption,
        string message,
        string userName,
        string targetName) =>
        throw new NotSupportedException("Credential prompts are not supported in MCP server mode.");

    /// <inheritdoc />
    public override PSCredential PromptForCredential(
        string caption,
        string message,
        string userName,
        string targetName,
        PSCredentialTypes allowedCredentialTypes,
        PSCredentialUIOptions options) =>
        throw new NotSupportedException("Credential prompts are not supported in MCP server mode.");
}
