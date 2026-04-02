#
# PowerCode.PsMcp Module Manifest
# Generated with New-ModuleManifest, then hand-tuned.
#
@{
    # The binary assembly that contains the cmdlet implementation.
    RootModule        = 'PowerCode.PsMcp.dll'

    ModuleVersion     = '0.1.2'
    GUID              = '3f7c1b9a-d4e8-4c5b-8f6a-2e0dc93a1b7e'
    Author            = 'PowerCode'
    CompanyName       = 'PowerCode'
    Copyright         = '(c) 2026 PowerCode. All rights reserved.'
    Description       = 'Exposes PowerShell commands as Model Context Protocol (MCP) tools over stdio.'

    # Requires PowerShell 7.4 (runs on .NET 8).
    PowerShellVersion = '7.4'

    # Export the public cmdlet entry points.
    CmdletsToExport   = @('Start-McpServer', 'New-McpToolConfig')
    FunctionsToExport = @()
    AliasesToExport   = @()
    VariablesToExport = @()

    Filelist          = @(
        'PowerCode.PsMcp.psd1',
        'PowerCode.PsMcp.dll',        
        'System.Net.ServerSentEvents.dll',
        'System.Text.Encodings.Web.dll'
        'Microsoft.Extensions.AI.Abstractions.dll'
        'Microsoft.Extensions.Configuration.Abstractions.dll'
        'Microsoft.Extensions.Configuration.Binder.dll'
        'Microsoft.Extensions.Configuration.dll'
        'Microsoft.Extensions.DependencyInjection.Abstractions.dll'
        'Microsoft.Extensions.DependencyInjection.dll'
        'Microsoft.Extensions.Logging.Abstractions.dll'
        'Microsoft.Extensions.Logging.Configuration.dll'
        'Microsoft.Extensions.Logging.Console.dll'
        'Microsoft.Extensions.Logging.dll'
        'Microsoft.Extensions.Options.ConfigurationExtensions.dll'
        'Microsoft.Extensions.Options.dll'
        'Microsoft.Extensions.Primitives.dll'
        'ModelContextProtocol.Core.dll'        
        'en-US\PowerCode.PsMcp.dll-Help.xml'
    )

    PrivateData = @{
        PSData = @{
            Tags         = @('MCP', 'AI', 'LLM', 'ModelContextProtocol')
            ProjectUri   = 'https://github.com/PowerCode/PowerCode.PsMcp'
            LicenseUri   = 'https://github.com/PowerCode/PowerCode.PsMcp/blob/main/LICENSE'
            ReleaseNotes = 'Initial release: Start-McpServer cmdlet.'
        }
    }
}
