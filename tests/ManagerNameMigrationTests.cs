using Xunit;

namespace Tessera.Tests;

/// `tessera migrate --step manager`: a memory manager handed to a routine is `manager`, not `alloc`, in its
/// declaration, its uses, the labels of calls, and the doc comment's `:param` line. `MemoryManager.alloc` keeps its name.
public class ManagerNameMigrationTests
{
    private static string Migrate(string source, List<string>? report = null, string? index = null)
    {
        var migration = new ManagerNameMigration();
        migration.Index("t.te", index ?? source, false);
        return migration.Rewrite(source, report ?? [], "t.te");
    }

    [Fact]
    public void Renames_a_parameter_its_uses_and_its_doc_line()
    {
        const string before = """
            ### Makes a list.
            ### :param alloc: Where the list takes memory.
            ### :returns: The list.
            routine make(alloc: @MemoryManager) -> List[S64]
                xs: List[S64] = List[S64].construct(alloc)
                taken: Addr = alloc.alloc(size: 8, align: 8)
                return xs
            """;
        const string after = """
            ### Makes a list.
            ### :param manager: Where the list takes memory.
            ### :returns: The list.
            routine make(manager: @MemoryManager) -> List[S64]
                xs: List[S64] = List[S64].construct(manager)
                taken: Addr = manager.alloc(size: 8, align: 8)
                return xs
            """;
        Assert.Equal(after, Migrate(before));
    }

    [Fact]
    public void Renames_a_local_a_label_and_a_field()
    {
        const string before = """
            record Holder
                alloc: @MemoryManager

            routine Holder.free(data: @Byte) -> None
                deallocate(data, alloc: me.alloc.peek())
                return

            routine start() -> None
                alloc: @MemoryManager = HEAP
                local xs: @List[S64] <- .construct(alloc: alloc)
                drop xs
                return
            """;
        const string after = """
            record Holder
                manager: @MemoryManager

            routine Holder.free(data: @Byte) -> None
                deallocate(data, manager: me.manager.peek())
                return

            routine start() -> None
                manager: @MemoryManager = HEAP
                local xs: @List[S64] <- .construct(manager: manager)
                drop xs
                return
            """;
        Assert.Equal(after, Migrate(before));
    }

    [Fact]
    public void Lists_a_routine_that_has_a_manager_already()
    {
        const string source = """
            routine start() -> None
                local manager: @Budget <- (left: 0)
                local alloc: @MemoryManager <- manager.to[MemoryManager]()
                return
            """;
        var report = new List<string>();
        Assert.Equal(source, Migrate(source, report));
        Assert.Single(report);
        Assert.Contains("t.te:1: this declaration has an alloc memory manager and a manager already", report[0]);
    }

    [Fact]
    public void Lists_a_label_when_some_alloc_is_not_a_manager()
    {
        const string source = """
            routine start() -> None
                drop grow(alloc: 4)
                return
            """;
        const string index = """
            routine grow(alloc: S64) -> S64
                return alloc
            """;
        var report = new List<string>();
        Assert.Equal(source, Migrate(source, report, index));
        Assert.Single(report);
        Assert.Contains("the alloc: label may name a parameter that isn't a memory manager", report[0]);
    }
}
