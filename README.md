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

### Rename tools with NameMap

```powershell
Start-McpServer -Module MyModule -NameMap @{ 'list-items' = 'Get-Item' }
```

Keys are the friendly MCP tool names; values are the real PowerShell command names.

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

## Parameters

| Parameter | Type | Description |
|-----------|------|-------------|
| `-Module` | `string[]` | Module name(s) or path(s) to import before discovery. |
| `-Include` | `string[]` | Wildcard patterns for commands to include. |
| `-Exclude` | `string[]` | Wildcard patterns for commands to exclude. |
| `-NameMap` | `hashtable` | Maps friendly MCP tool names (keys) to command names (values). |
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
4. `-Include`/`-Exclude` patterns refine the set; `-NameMap` renames tools.
5. Each tool's description and parameter schema are populated from `Get-Help`.
6. An MCP server is started over stdin/stdout using the [ModelContextProtocol .NET SDK](https://github.com/modelcontextprotocol/csharp-sdk).

## License

(c) 2026 PowerCode. All rights reserved. See [LICENSE](LICENSE).
