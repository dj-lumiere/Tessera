using Xunit;

namespace Lapila.Tests;

/// <summary>
/// The test classes that change the process's working directory. The builder reads it (the paths it shows, where it
/// looks for the manifest and the standard library), and it is one per process, so these classes run one at a time
/// and never beside another class: a build in one class must not see the folder another class just moved to.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WorkingDirectoryCollection
{
    public const string Name = "Working directory";
}
