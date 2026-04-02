---
document type: cmdlet
external help file: PowerCode.PsMcp.dll-Help.xml
HelpUri: ''
Locale: en-US
Module Name: powercode.psmcp
ms.date: 04-02-2026
PlatyPS schema version: 2024-05-01
title: Start-McpServer
---

# Start-McpServer

## SYNOPSIS

Exposes PowerShell commands as MCP tools over JSON-RPC/stdio using automatic command discovery.

## SYNTAX

```
Start-McpServer [[-Module] <string[]>] [-Include <string[]>] [-Exclude <string[]>]
 [-NameMap <hashtable>] [-ScriptPath <string>] [-ScriptBlock <scriptblock>] [-Depth <int>]
 [<CommonParameters>]
```

## ALIASES

None.

## DESCRIPTION

`Start-McpServer` starts a Model Context Protocol (MCP) server that exposes PowerShell commands
as tools consumable by MCP clients (e.g. AI agents). The cmdlet blocks until the client
disconnects or the user presses Ctrl+C.

Tools are discovered automatically by diffing the commands available before and after loading
your module or script. Only commands **introduced** by the load are exported — built-in commands
are excluded by default.

At least one of `-Module`, `-ScriptBlock`, or `-ScriptPath` must be provided.

**Include/Exclude filtering**

`-Include` and `-Exclude` accept PowerShell wildcard patterns (e.g. `Get-*`). Include patterns
can only pull in commands from the discovery delta **or** from a module explicitly named in
`-Module`. A baseline command such as `Get-Date` can therefore only be included when its owning
module (`Microsoft.PowerShell.Utility`) is also passed via `-Module`.

**NameMap**

`-NameMap` is a hashtable that maps friendly MCP tool names (keys) to real PowerShell command
names (values). Commands not present in the map keep their original name.

## EXAMPLES

### Example 1 — Expose all commands from a module

```powershell
Start-McpServer -Module MyToolsModule
```

Imports `MyToolsModule` and exposes every command it introduces as an MCP tool.

### Example 2 — Load a script file and expose its functions

```powershell
Start-McpServer -ScriptPath ./tools.ps1
```

Dot-sources `tools.ps1` into a fresh runspace and exposes the functions it defines.

### Example 3 — Define tools inline with a ScriptBlock

```powershell
Start-McpServer -ScriptBlock {
    function Get-CurrentWeather {
        <#
        .SYNOPSIS
        Gets the current weather for a location.

        .DESCRIPTION
        Calls a weather service to retrieve current conditions for the specified city or region.

        .PARAMETER Location
        The city or region to get weather for.

        .PARAMETER Units
        Temperature units: Celsius or Fahrenheit.

        .EXAMPLE
        Get-CurrentWeather -Location "Seattle" -Units Celsius
        #>
        [CmdletBinding()]
        param(
            [Parameter(Mandatory)]
            [string]$Location,

            [ValidateSet('Celsius', 'Fahrenheit')]
            [string]$Units = 'Celsius'
        )
        @{ Location = $Location; Temperature = 72; Units = $Units }
    }
}
```

Defines `Get-CurrentWeather` inline and exposes it as the only MCP tool.
`Start-McpServer` uses `Get-Help` to extract the function's synopsis, description, and
per-parameter descriptions from the comment-based help block — these become the MCP tool's
`description` and input schema `description` fields visible to AI clients.

### Example 4 — Include a baseline command by importing its module

```powershell
Start-McpServer -Module Microsoft.PowerShell.Utility -Include 'Get-Date'
```

Imports `Microsoft.PowerShell.Utility` and uses `-Include` to explicitly pull in `Get-Date`
(which would otherwise be excluded as a baseline command).

### Example 5 — Rename tools with NameMap and filter with wildcards

```powershell
Start-McpServer -Module MyModule -Include 'Get-*' -Exclude 'Get-Secret' `
                -NameMap @{ 'list-items' = 'Get-Item' }
```

Imports `MyModule`, includes all `Get-*` commands (excluding `Get-Secret`), and renames
`Get-Item` to `list-items` in the MCP protocol.

### Example 6 — Increase JSON depth for deeply nested output

```powershell
Start-McpServer -ScriptPath ./tools.ps1 -Depth 5
```

## PARAMETERS

### -Depth

Maximum serialisation depth passed to `ConvertTo-Json` when building structured output for each
tool result. Higher values capture more nested object detail at the cost of larger payloads.
Defaults to `2`. Valid range: 1–10.

```yaml
Type: System.Int32
DefaultValue: '2'
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: (All)
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -Exclude

One or more PowerShell wildcard patterns. Commands whose names match any pattern are removed
from the discovered command set. Exclusions are applied after `-Include`.

```yaml
Type: System.String[]
DefaultValue: ''
SupportsWildcards: true
Aliases: []
ParameterSets:
- Name: (All)
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -Include

One or more PowerShell wildcard patterns. Commands from the discovery delta or from a module
explicitly listed in `-Module` whose names match any pattern are added to the tool set.
A baseline command (e.g. `Get-Date`) can only be included when its owning module
(e.g. `Microsoft.PowerShell.Utility`) is also passed in `-Module`.

```yaml
Type: System.String[]
DefaultValue: ''
SupportsWildcards: true
Aliases: []
ParameterSets:
- Name: (All)
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -Module

One or more module names or paths to import into the discovery runspace before command
discovery runs. All commands introduced by these modules are eligible for export. At least one
of `-Module`, `-ScriptBlock`, or `-ScriptPath` must be provided.

```yaml
Type: System.String[]
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: (All)
  Position: 0
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -NameMap

A hashtable that maps friendly MCP tool names (keys) to real PowerShell command names (values).
Commands not present in the map keep their original name. Key lookup is case-insensitive.

Example: `@{ 'list-items' = 'Get-Item'; 'now' = 'Get-Date' }`

```yaml
Type: System.Collections.Hashtable
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: (All)
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -ScriptBlock

A script block that is invoked in the discovery runspace before command discovery runs.
Functions and variables defined in the block are available to the MCP tools.
Mutually exclusive with `-ScriptPath`; if both are supplied, `-ScriptBlock` is ignored.
At least one of `-Module`, `-ScriptBlock`, or `-ScriptPath` must be provided.

```yaml
Type: System.Management.Automation.ScriptBlock
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: (All)
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### -ScriptPath

Path to a `.ps1` file that is dot-sourced into the discovery runspace before command discovery
runs. Use this to load helper functions that are not yet packaged as a module.
At least one of `-Module`, `-ScriptBlock`, or `-ScriptPath` must be provided.

```yaml
Type: System.String
DefaultValue: ''
SupportsWildcards: false
Aliases: []
ParameterSets:
- Name: (All)
  Position: Named
  IsRequired: false
  ValueFromPipeline: false
  ValueFromPipelineByPropertyName: false
  ValueFromRemainingArguments: false
DontShow: false
AcceptedValues: []
HelpMessage: ''
```

### CommonParameters

This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable,
-InformationAction, -InformationVariable, -OutBuffer, -OutVariable, -PipelineVariable,
-ProgressAction, -Verbose, -WarningAction, and -WarningVariable. For more information, see
[about_CommonParameters](https://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### None

This cmdlet does not accept pipeline input.

## OUTPUTS

### None

This cmdlet does not produce output. It blocks until the MCP client disconnects or the
cancellation token fires.

## NOTES

The cmdlet communicates over the process's raw stdin/stdout handles so that the JSON-RPC
messages bypass PowerShell's occupied pipeline. Do not write to stdout while the server is
running; use `-Verbose` or redirect output to a file instead.

The discovery runspace is isolated: it does not share state with the calling session.

## RELATED LINKS

- [Model Context Protocol specification](https://spec.modelcontextprotocol.io)
- [PowerCode.PsMcp on GitHub](https://github.com/powercode/PowerCode.PsMcp)

