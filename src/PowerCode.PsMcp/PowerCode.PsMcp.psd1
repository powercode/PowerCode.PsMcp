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

    # Only export the Start-McpServer entry-point.
    CmdletsToExport   = @('Start-McpServer')
    FunctionsToExport = @()
    AliasesToExport   = @()
    VariablesToExport = @()

    Filelist          = @('PowerCode.PsMcp.dll', 'PowerCode.PsMcp.psd1', 'System.Net.ServerSentEvents.dll', 'System.Text.Encodings.Web.dll')

    PrivateData = @{
        PSData = @{
            Tags         = @('MCP', 'AI', 'LLM', 'ModelContextProtocol')
            ProjectUri   = 'https://github.com/PowerCode/PowerCode.PsMcp'
            LicenseUri   = 'https://github.com/PowerCode/PowerCode.PsMcp/blob/main/LICENSE'
            ReleaseNotes = 'Initial release: Start-McpServer cmdlet.'
        }
    }
}
