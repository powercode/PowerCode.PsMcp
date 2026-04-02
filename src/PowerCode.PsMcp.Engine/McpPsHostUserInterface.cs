using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Security;

namespace PowerCode.PsMcp;

/// <summary>
/// Custom <see cref="PSHostUserInterface"/> that routes all PowerShell output streams to
/// stderr, preserving stdout for MCP JSON-RPC communication. Interactive operations
/// (prompts, credentials, secure string input) throw <see cref="NotSupportedException"/>
/// because no interactive user is present in MCP server mode.
/// </summary>
internal sealed class McpPsHostUserInterface : PSHostUserInterface
{
    private readonly McpPsHostRawUserInterface _rawUi = new();

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
    public override void WriteErrorLine(string value) =>
        Console.Error.WriteLine($"ERROR: {value}");

    /// <inheritdoc />
    public override void WriteWarningLine(string message) =>
        Console.Error.WriteLine($"WARNING: {message}");

    /// <inheritdoc />
    public override void WriteVerboseLine(string message) =>
        Console.Error.WriteLine($"VERBOSE: {message}");

    /// <inheritdoc />
    public override void WriteDebugLine(string message) =>
        Console.Error.WriteLine($"DEBUG: {message}");

    /// <inheritdoc />
    public override void WriteProgress(long sourceId, ProgressRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Console.Error.WriteLine(
            $"PROGRESS [{record.ActivityId}]: {record.Activity} – {record.StatusDescription}");
    }

    /// <inheritdoc />
    public override void WriteInformation(InformationRecord record)
    {
        if (record is not null)
        {
            Console.Error.WriteLine($"INFO: {record.MessageData}");
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
