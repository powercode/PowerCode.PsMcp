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
  'Get-Item' = New-McpToolConfig -Name 'list-items' -Property Name, FullName
  'Get-Date' = New-McpToolConfig -Name 'now' -Depth 4
}

Start-McpServer -Module MyModule -CommandConfiguration $toolConfig
```

Keys are the real PowerShell command names; values are `McpToolConfig` objects created with
`New-McpToolConfig`. Each config can rename the tool, select or exclude properties, and override
the JSON depth for that command only.

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
| `-ScriptBlock` | `scriptblock` | Script block invoked before starting the server. |
| `-Depth` | `int` | JSON serialisation depth for tool output (1–10, default 2). |

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

## Perfect ParameterSet Input and Output Mapping

When a command has multiple parameter sets, PowerCode.PsMcp uses ParameterSetName as an input discriminator and can project output schema per set when metadata is available.

### How to get deterministic per-set output mapping

1. Define explicit parameter sets with CmdletBinding(DefaultParameterSetName=...).
2. Declare OutputType(..., ParameterSetName=...) for every parameter set.
3. Ensure each OutputType ParameterSetName exactly matches the parameter set name.
4. If a set can return multiple CLR types, declare all of them for that set.
5. Keep OutputType declarations aligned with real runtime output.

For multi-set commands, PowerCode.PsMcp adds ParameterSetName to structured output so clients can align the selected input set with the matching output schema variant.

### Fallback behavior

If perfect metadata is not available, output schema generation follows a deterministic fallback ladder:

1. Tier 1: Full per-set mapping from OutputType(..., ParameterSetName=...).
2. Tier 2: Partial per-set mapping, with missing sets filled from __AllParameterSets declarations.
3. Tier 3: No per-set linkage available, so output items use a oneOf union of CommandInfo.OutputType.
4. Tier 4: No output metadata, so outputSchema is omitted.

### When to split into separate tools

If parameter sets imply different permissions, side effects, or operational semantics, prefer separate tools instead of a single multi-set tool.

## License

(c) 2026 PowerCode. All rights reserved. See [LICENSE](LICENSE).
