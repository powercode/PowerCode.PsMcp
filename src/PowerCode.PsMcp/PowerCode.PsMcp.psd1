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
        'Dependencies\System.Net.ServerSentEvents.dll',
        'Dependencies\System.Text.Encodings.Web.dll',
        'Dependencies\System.Diagnostics.DiagnosticSource.dll',
        'Dependencies\System.IO.Pipelines.dll',
        'Dependencies\System.Text.Json.dll',
        'Dependencies\Microsoft.Extensions.AI.Abstractions.dll',
        'Dependencies\Microsoft.Extensions.Caching.Abstractions.dll',
        'Dependencies\Microsoft.Extensions.Configuration.Abstractions.dll',
        'Dependencies\Microsoft.Extensions.Configuration.Binder.dll',
        'Dependencies\Microsoft.Extensions.Configuration.dll',
        'Dependencies\Microsoft.Extensions.DependencyInjection.Abstractions.dll',
        'Dependencies\Microsoft.Extensions.DependencyInjection.dll',
        'Dependencies\Microsoft.Extensions.Diagnostics.Abstractions.dll',
        'Dependencies\Microsoft.Extensions.FileProviders.Abstractions.dll',
        'Dependencies\Microsoft.Extensions.Hosting.Abstractions.dll',
        'Dependencies\Microsoft.Extensions.Logging.Abstractions.dll',
        'Dependencies\Microsoft.Extensions.Logging.Configuration.dll',
        'Dependencies\Microsoft.Extensions.Logging.Console.dll',
        'Dependencies\Microsoft.Extensions.Logging.dll',
        'Dependencies\Microsoft.Extensions.Options.ConfigurationExtensions.dll',
        'Dependencies\Microsoft.Extensions.Options.dll',
        'Dependencies\Microsoft.Extensions.Primitives.dll',
        'Dependencies\ModelContextProtocol.dll',
        'Dependencies\ModelContextProtocol.Core.dll',
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
