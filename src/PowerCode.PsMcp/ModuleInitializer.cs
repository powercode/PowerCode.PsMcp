using System.Management.Automation;
using System.Reflection;
using System.Runtime.Loader;

namespace PowerCode.PsMcp;

/// <summary>
/// Implements <see cref="IModuleAssemblyInitializer"/> and <see cref="IModuleAssemblyCleanup"/>
/// to register an <see cref="AssemblyLoadContext"/> isolation handler that loads module
/// dependency assemblies from the <c>Dependencies</c> subfolder.
/// </summary>
/// <remarks>
/// PowerShell discovers this class via reflection on module import and calls
/// <see cref="OnImport"/> to initialize the resolver. On module removal,
/// <see cref="OnRemove"/> is called to clean up.
/// </remarks>
public sealed class ModuleInitializer : IModuleAssemblyInitializer, IModuleAssemblyCleanup
{
    private static readonly string s_dependencyDirPath = Path.GetFullPath(
        Path.Combine(
            Path.GetDirectoryName(typeof(ModuleInitializer).Assembly.Location)!,
            "Dependencies"));

    private static readonly PsMcpLoadContext s_loadContext = new(s_dependencyDirPath);

    /// <summary>
    /// Called by PowerShell when the module is imported.
    /// Registers the dependency resolver on the default <see cref="AssemblyLoadContext"/>.
    /// </summary>
    public void OnImport()
    {
        AssemblyLoadContext.Default.Resolving += ResolveDependency;
    }

    /// <summary>
    /// Called by PowerShell when the module is removed.
    /// Unregisters the dependency resolver.
    /// </summary>
    /// <param name="psModuleInfo">The module being removed.</param>
    public void OnRemove(PSModuleInfo psModuleInfo)
    {
        AssemblyLoadContext.Default.Resolving -= ResolveDependency;
    }

    /// <summary>
    /// Resolves dependencies loaded into the custom <see cref="PsMcpLoadContext"/>
    /// when the default ALC cannot resolve them.
    /// </summary>
    /// <param name="defaultAlc">The default <see cref="AssemblyLoadContext"/>.</param>
    /// <param name="assemblyName">The assembly name to resolve.</param>
    /// <returns>
    /// The loaded assembly from the custom ALC if found in the Dependencies folder;
    /// otherwise, <c>null</c> to allow other resolvers to handle the request.
    /// </returns>
    private static Assembly? ResolveDependency(AssemblyLoadContext defaultAlc, AssemblyName assemblyName)
    {
        // Only handle assemblies that exist in our Dependencies folder.
        string assemblyPath = Path.Combine(s_dependencyDirPath, $"{assemblyName.Name}.dll");
        if (File.Exists(assemblyPath))
        {
            return s_loadContext.LoadFromAssemblyName(assemblyName);
        }

        return null;
    }
}
