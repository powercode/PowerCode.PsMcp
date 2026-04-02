using System.Reflection;
using System.Runtime.Loader;

namespace PowerCode.PsMcp;

/// <summary>
/// Custom <see cref="AssemblyLoadContext"/> that resolves module dependency assemblies
/// from the <c>Dependencies</c> subfolder, isolating them from the default ALC to prevent
/// version conflicts with other PowerShell modules.
/// </summary>
internal sealed class PsMcpLoadContext : AssemblyLoadContext
{
    private readonly string _dependencyDirPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="PsMcpLoadContext"/> class.
    /// </summary>
    /// <param name="dependencyDirPath">The full path to the directory containing dependency assemblies.</param>
    internal PsMcpLoadContext(string dependencyDirPath)
        : base("PsMcpLoadContext", isCollectible: false)
    {
        _dependencyDirPath = dependencyDirPath;
    }

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        string assemblyPath = Path.Combine(_dependencyDirPath, $"{assemblyName.Name}.dll");
        if (File.Exists(assemblyPath))
        {
            return LoadFromAssemblyPath(assemblyPath);
        }

        return null;
    }
}
