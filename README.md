# PowerCode.PsMcp

A PowerShell binary module that exposes PowerShell commands as **Model Context Protocol (MCP) tools** over JSON-RPC/stdio, making them consumable by AI agents and MCP clients such as GitHub Copilot, Claude Desktop, and others.

## Requirements

- PowerShell 7.4+
- .NET 8 SDK (to build from source)

## Installation

### From source

```powershell
dotnet build PowerCode.PsMcp.slnx -c Release
Import-Module ./artifacts/bin/PowerCode.PsMcp/release/PowerCode.PsMcp.psd1
```

## Usage

`Start-McpServer` is the single entry point. It blocks until the MCP client disconnects or you press Ctrl+C.

At least one of `-Module`, `-ScriptBlock`, or `-ScriptPath` must be provided.

By default, tool calls can run in parallel using a runspace pool (up to 8 runspaces). Use `-NoParallel` to force serialized execution in a single runspace.

### Expose all commands from a module

```powershell
Start-McpServer -Module MyToolsModule
```

Imports `MyToolsModule` and discovers every command it introduces — built-in baseline commands are excluded automatically.

### Load a script file

```powershell
Start-McpServer -ScriptPath ./tools.ps1
```

Dot-sources `tools.ps1` into a fresh runspace and exposes the functions it defines.

### Define tools inline

```powershell
Start-McpServer -ScriptBlock {
    function Get-CurrentWeather {
        <#
        .SYNOPSIS
        Gets the current weather for a location.
        .PARAMETER Location
        The city or region to get weather for.
        #>
        param([Parameter(Mandatory)][string]$Location)
        @{ Location = $Location; Temperature = 72 }
    }
}
```

Comment-based help (`.SYNOPSIS`, `.DESCRIPTION`, `.PARAMETER`) is extracted via `Get-Help` and becomes the tool's description and input schema visible to the AI client.

### Filter with Include/Exclude

```powershell
Start-McpServer -Module MyModule -Include 'Get-*' -Exclude 'Get-Secret'
```

`-Include` and `-Exclude` accept PowerShell wildcard patterns. Include patterns are restricted to commands introduced by module/script loading. To include a baseline command (e.g. `Get-Date`), you must also declare its owning module via `-Module`:

```powershell
Start-McpServer -Module Microsoft.PowerShell.Utility -Include 'Get-Date'
```

### Configure tools with CommandConfiguration

```powershell
$toolConfig = @{
  'Get-Item' = New-McpToolConfig -Name 'list-items' -Property Name, FullName -ExcludeProperty PSPath
  'Get-Process' = New-McpToolConfig -Name 'proc-by-name' -ParameterSets Name -Depth 4
}

Start-McpServer -Module MyModule -CommandConfiguration $toolConfig
```

Keys are the real PowerShell command names; values are `McpToolConfig` objects created with
`New-McpToolConfig`. Each config can rename the tool, select or exclude properties, filter input
schema to specific parameter sets, and override the JSON depth for that command only.

### Enable startup diagnostics

```powershell
Start-McpServer -Module MyModule -DiagnosticLogging
```

Enables Trace-level diagnostics, including discovery, schema mapping, and tool execution.
Without this switch, diagnostic logging includes Warning and higher levels. Logs go to stderr,
keeping stdout reserved for MCP JSON-RPC.

### Save diagnostics to a file

```powershell
Start-McpServer -Module MyModule -LogPath ./psmcp.log -DiagnosticLogging
```

`-LogPath` additionally appends UTF-8 diagnostics to a file, resolving relative paths from the
current PowerShell location. The parent directory must already exist. Each record includes a UTC
timestamp, severity, category, event ID, message, and any exception details. Writes from parallel
tool calls are serialized and flushed immediately. Use a separate file for each server process.

The file rotates before a write would exceed 10 MiB, retaining five backups (`psmcp.log.1` through
`psmcp.log.5`, newest first). A single oversized record is kept intact. An unwritable file fails
startup; a later I/O failure disables file logging and reports the failure on stderr.

Deprecated MCP `notifications/message` logging is no longer emitted. PowerShell stream messages
remain in final tool results, and progress notifications remain supported. Direct PowerShell host
output still goes to stderr; the file captures records routed through the diagnostic logger.

### Force serialized tool execution

```powershell
Start-McpServer -Module MyModule -NoParallel
```

Disables runspace-pool concurrency and executes all tool calls in a single runspace. Use this when tools depend on shared session state.

### Increase JSON serialisation depth

```powershell
Start-McpServer -ScriptPath ./tools.ps1 -Depth 5
```

Valid range is 1–10; default is 2.

## Configuring an MCP client

The server communicates over **stdio**. Point your MCP client at the `pwsh` executable and pass the `Start-McpServer` invocation as the command. Example VS Code `mcp.json` entry:

```json
{
  "servers": {
    "my-tools": {
      "type": "stdio",
      "command": "pwsh",
      "args": [
        "-NoProfile",
        "-NonInteractive",
        "-Command",
        "Import-Module PowerCode.PsMcp; Start-McpServer -Module MyToolsModule"
      ]
    }
  }
}
```

## Real-world example: process information tools

This example wires up two MCP tools that expose process information.
It uses a dedicated script file so the configuration stays out of `mcp.json`.

### 1. `mcp.json`

Place this file in your workspace's `.vscode/` directory (or wherever your MCP client expects it).

```json
{
  "servers": {
    "processes": {
      "type": "stdio",
      "command": "pwsh",
      "args": [
        "-noprofile",
        "-noni",
        "-file",
        "${workspaceFolder}/tools/pwsh-mcp/Proc.ps1"
      ]
    }
  }
}
```

| Field | Purpose |
|-------|---------|
| `"type": "stdio"` | The server communicates over standard input/output — required for `Start-McpServer`. |
| `"command": "pwsh"` | Launch PowerShell 7. |
| `"-noprofile"` / `"-noni"` | Skip the user profile and disable interactive prompts for a clean, fast startup. |
| `"-file"` | Run the given `.ps1` file instead of inline `-Command` text to keep `mcp.json` simple and allow complex configuration in a real file. |
| `"${workspaceFolder}/…/Proc.ps1"` | Path resolved by the MCP client; put the script anywhere that suits your project layout. |

### 2. `Proc.ps1`

```powershell
#Requires -Module PowerCode.PsMcp

Start-McpServer -Module Microsoft.PowerShell.Management -Include Get-Process -CommandConfiguration @{
    'Get-Process'      = New-McpToolConfig -Name 'process' -Property Id, Name, CPU, StartTime, Path, CommandLine, FileVersionInfo -Depth 1 -ParameterSets Name, Id
    'processFileVersion' = New-McpToolConfig -Depth 1 -Property CompanyName, FileName, FileVersion, ProductVersion
} -ScriptBlock {
    function processFileVersion {
        <#
        .SYNOPSIS
        Gets the file version information for one or more processes. Specify the process by name or process ID (PID).
        .PARAMETER Name
        The name of the process(es) to get the file version information for.
        .PARAMETER Id
        The ID of the process(es) to get the file version information for.
        #>
        [OutputType([System.Diagnostics.FileVersionInfo])]
        param(
            [Parameter(Mandatory = $true, ParameterSetName = 'Name')]
            [string[]] $Name
            ,
            [Parameter(Mandatory = $true, ParameterSetName = 'Id')]
            [Int[]] $Id
        )

        Get-Process -FileVersionInfo @PSBoundParameters
    }
}
```

#### `#Requires -Module PowerCode.PsMcp`

Ensures the module is available before the script body runs. If you installed from source, make sure `Import-Module` has been called for the current session or that the module directory is on `$env:PSModulePath`.

#### `-Module Microsoft.PowerShell.Management -Include Get-Process`

Imports `Microsoft.PowerShell.Management` into the server's runspace, then restricts the discovered command set to only `Get-Process`. Without `-Include` every command introduced by the module would become a tool.

#### `-CommandConfiguration`

A hashtable that customises individual tools.

**Key `'Get-Process'`** — controls the tool built from the built-in `Get-Process` command:

| `New-McpToolConfig` argument | Effect |
|------------------------------|--------|
| `-Name 'process'` | Renames the MCP tool from `Get-Process` to `process`. |
| `-Property Id, Name, CPU, StartTime, Path, CommandLine, FileVersionInfo` | Output is piped through `Select-Object` with these properties, keeping tool responses small and predictable. |
| `-Depth 1` | JSON serialisation is limited to one level for this tool, overriding the global default. |
| `-ParameterSets Name, Id` | Only parameters that belong to the `Name` or `Id` parameter sets are included in the tool's input schema sent to the AI client — common parameters like `-ErrorAction` are hidden. |

**Key `'processFileVersion'`** — controls the tool built from the custom `processFileVersion` function defined in `-ScriptBlock`:

| `New-McpToolConfig` argument | Effect |
|------------------------------|--------|
| `-Depth 1` | Serialise `FileVersionInfo` objects one level deep. |
| `-Property CompanyName, FileName, FileVersion, ProductVersion` | Surface only the four most useful fields instead of the full `FileVersionInfo` property bag. |

#### `-ScriptBlock { function processFileVersion { … } }`

The script block is invoked inside the server's runspace before tool discovery runs. Any functions it defines become candidates for MCP tools. Here `processFileVersion` wraps `Get-Process -FileVersionInfo @PSBoundParameters` — splatting `$PSBoundParameters` forwards whichever parameter set the caller used (`-Name` or `-Id`) directly to `Get-Process`.

The function's **comment-based help** (`.SYNOPSIS`, `.PARAMETER`) is extracted via `Get-Help` at startup and becomes the tool description and per-parameter descriptions in the schema that the AI client sees. Well-written help therefore directly improves how accurately an AI agent invokes your tools.

`[OutputType([System.Diagnostics.FileVersionInfo])]` is optional metadata; it does not affect runtime behaviour but documents intent.

## Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `-Module` | `string[]` | Module name(s) or path(s) to import before discovery. |
| `-Include` | `string[]` | Wildcard patterns for commands to include. |
| `-Exclude` | `string[]` | Wildcard patterns for commands to exclude. |
| `-CommandConfiguration` | `hashtable` | Maps command names (keys) to `McpToolConfig` values for per-command naming, property selection, and depth overrides. |
| `-ScriptPath` | `string` | Path to a `.ps1` script to dot-source before starting. |
| `-ScriptBlock` | `scriptblock` | Script block invoked before starting the server. If both `-ScriptPath` and `-ScriptBlock` are provided, `-ScriptPath` is used. |
| `-Depth` | `int` | JSON serialisation depth for tool output (1–10, default 2). |
| `-DiagnosticLogging` | `switch` | Enables startup diagnostics for command discovery and schema mapping. |
| `-NoParallel` | `switch` | Disables parallel tool invocation and serializes calls in a single runspace. |

## Building and testing

```powershell
# Build
dotnet build PowerCode.PsMcp.slnx -c Debug

# Test
dotnet test PowerCode.PsMcp.slnx -c Debug
```

## How it works

1. A baseline snapshot of all commands in a clean runspace is taken.
2. Your modules/script are loaded into a new runspace.
3. The delta (new commands only) forms the candidate tool set.
4. `-Include`/`-Exclude` patterns refine the set; `-CommandConfiguration` applies per-command naming and output shaping.
5. Each tool's description and parameter schema are populated from `Get-Help`.
6. An MCP server is started over stdin/stdout using the [ModelContextProtocol .NET SDK](https://github.com/modelcontextprotocol/csharp-sdk).

## ParameterSet Input and Output Mapping

When a command has multiple parameter sets, PowerCode.PsMcp exposes each set as a separate variant in a `oneOf` input schema and can project a per-set output schema when `OutputType` metadata is available.

### Input schema for multi-set commands

The input schema is `{"type":"object","oneOf":[...]}` with one variant per active parameter set. Each variant:

- Contains only the parameters that belong to that set.
- Sets `additionalProperties: false` so clients know exactly which parameters are valid.
- Uses the parameter set name as its `description` (default set is labeled `Name (default)`).

The AI client selects a parameter set by providing the right required parameters — no explicit `ParameterSetName` field is needed or declared in the schema.

#### Optional ParameterSetName passthrough

A client may include `ParameterSetName` as an extra key in the input object as a tracking hint. The server strips it before invoking PowerShell (preventing a binding error) and echoes it into the `ParameterSetName` field of the structured output envelope. The server does not auto-detect which parameter set was resolved at runtime.

### How to get deterministic per-set output mapping

1. Define explicit parameter sets with `CmdletBinding(DefaultParameterSetName=...)`.
2. Declare `OutputType(..., ParameterSetName=...)` for every parameter set.
3. Ensure each `OutputType` `ParameterSetName` exactly matches the parameter set name.
4. If a set can return multiple CLR types, declare all of them for that set.
5. Keep `OutputType` declarations aligned with real runtime output.

When ≥ 2 parameter sets have output type mappings, the output schema becomes `{"type":"object","oneOf":[...]}` where each variant has a `ParameterSetName` const field (the set name) and a per-set `items` array schema. Parameter sets with no mapping are omitted from the `oneOf`.

### Fallback behavior

If complete metadata is not available, output schema generation follows a deterministic fallback ladder:

1. Tier 1: Full per-set mapping — all active sets have explicit `OutputType(..., ParameterSetName=...)` → `oneOf` output schema.
2. Tier 2: Partial per-set mapping — some sets fall back to `__AllParameterSets` types; ≥ 2 sets covered → `oneOf` output schema; uncovered sets are omitted.
3. Tier 3: No per-set linkage — flat `{"items":[...]}` schema built from `CommandInfo.OutputType` (items may use `oneOf` internally when multiple types are declared).
4. Tier 4: No output metadata — `outputSchema` is omitted.

### When to split into separate tools

If parameter sets imply different permissions, side effects, or operational semantics, prefer separate tools instead of a single multi-set tool.

## License

(c) 2026 PowerCode. All rights reserved. See [LICENSE](LICENSE).
