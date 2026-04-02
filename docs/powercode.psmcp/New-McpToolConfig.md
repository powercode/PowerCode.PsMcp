---
document type: cmdlet
external help file: PowerCode.PsMcp.dll-Help.xml
HelpUri: ''
Locale: en-US
Module Name: PowerCode.PsMcp
ms.date: 04-02-2026
PlatyPS schema version: 2024-05-01
title: New-McpToolConfig
---

# New-McpToolConfig

## SYNOPSIS

Creates a per-command MCP tool configuration object for use with `Start-McpServer -CommandConfiguration`.

## SYNTAX

### __AllParameterSets

```
New-McpToolConfig [[-Name] <string>] [-Property <string[]>] [-ExcludeProperty <string[]>]
 [-Depth <int>] [<CommonParameters>]
```

## ALIASES

None.

## DESCRIPTION

`New-McpToolConfig` creates an `McpToolConfig` object that controls how a PowerShell command is
exposed as an MCP tool.

Use it to configure one or more of the following for a specific command:

- A friendly tool name (`-Name`)
- Included output properties (`-Property`)
- Excluded output properties (`-ExcludeProperty`)
- Per-command JSON depth (`-Depth`)

The object returned by this cmdlet is intended to be placed in the hashtable passed to
`Start-McpServer -CommandConfiguration`, where the hashtable key is the PowerShell command name.

## EXAMPLES

### Example 1 - Rename a command for MCP clients

```powershell
$cfg = New-McpToolConfig -Name 'list-items'
```

Creates an `McpToolConfig` object that renames the exposed MCP tool to `list-items`.

### Example 2 - Include specific output properties

```powershell
$cfg = New-McpToolConfig -Property Name, FullName
```

Restricts tool output to `Name` and `FullName`.

### Example 3 - Build command configuration for Start-McpServer

```powershell
$commandConfiguration = @{
  'Get-Item' = New-McpToolConfig -Name 'list-items' -Property Name, FullName -Depth 3
  'Get-Date' = New-McpToolConfig -Name 'now'
}

Start-McpServer -Module MyModule -CommandConfiguration $commandConfiguration
```

Maps PowerShell command names to per-command MCP tool configuration.

## PARAMETERS

### -Depth

JSON serialization depth override (1-10)

```yaml
Type: System.Int32
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

### -ExcludeProperty

Properties to exclude from tool output

```yaml
Type: System.String[]
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

### -Name

Friendly MCP tool name

```yaml
Type: System.String
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

### -Property

Properties to include in tool output

```yaml
Type: System.String[]
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

## OUTPUTS

### PowerCode.PsMcp.McpToolConfig

Returns an `McpToolConfig` instance.

## NOTES

If `-Depth` is not specified, the returned object has `Depth = $null`, so `Start-McpServer`
uses its global `-Depth` value for that command.

## RELATED LINKS

[Start-McpServer](Start-McpServer.md)

