using FluentAssertions;
using Tandem.Infrastructure;

#pragma warning disable MAAI001

namespace Tandem.Tests.Infrastructure;

public sealed class GitExcludedFileStoreTests
{
    [Fact]
    public async Task WriteAsync_StripsLeadingUnicodeBom()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tandem-file-store-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);

        try
        {
            var store = new GitExcludedFileStore(new BomlessFileSystemAgentFileStore(directory));

            await store.WriteAsync(
                "service.ts",
                "\uFEFFimport type { Todo } from './types';\n",
                default
            );

            var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, "service.ts"));
            bytes.Take(3).Should().NotEqual([0xEF, 0xBB, 0xBF]);
            (await File.ReadAllTextAsync(Path.Combine(directory, "service.ts")))
                .Should()
                .StartWith("import type");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

#pragma warning restore MAAI001
