// <copyright file="AriaPostgresMemoryTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Threading;
using MUnique.OpenMU.Persistence.EntityFramework.Aria;

/// <summary>Exercises real PostgreSQL memory with an isolated development database.</summary>
[TestFixture]
public class AriaPostgresMemoryTest
{
    /// <summary>Verifies persistence, persona isolation, summary watermark and deletion.</summary>
    [Test]
    public async Task MemorySurvivesNewStoreAndIsIsolatedAsync()
    {
        var connection = Environment.GetEnvironmentVariable("OPENMU_ARIA_TEST_DB");
        if (string.IsNullOrEmpty(connection))
        {
            Assert.Ignore("Set OPENMU_ARIA_TEST_DB to an isolated migrated development database.");
            return;
        }

        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        long through;
        await using (var store = new PostgresAriaMemoryStore(connection))
        {
            var empty = await store.LoadAsync(first, "aria", CancellationToken.None).ConfigureAwait(false);
            Assert.That(empty.Recent, Is.Empty);
            through = await store.AppendAsync(first, "aria", "叫我小明", "记住了，小明。", "test", CancellationToken.None).ConfigureAwait(false);
            await store.AppendAsync(second, "aria", "叫我小红", "你好，小红。", "test", CancellationToken.None).ConfigureAwait(false);
            await store.AppendAsync(first, "other-npc", "另一位NPC", "独立会话", "test", CancellationToken.None).ConfigureAwait(false);
            await store.SummarizeAsync(first, "aria", 0, through, "玩家自称小明。", CancellationToken.None).ConfigureAwait(false);
            // A stale summary update must not overwrite a newer watermark.
            await store.SummarizeAsync(first, "aria", 0, through + 1, "错误摘要", CancellationToken.None).ConfigureAwait(false);
        }

        await using (var store = new PostgresAriaMemoryStore(connection))
        {
            var memory = await store.LoadAsync(first, "aria", CancellationToken.None).ConfigureAwait(false);
            Assert.That(memory.Summary, Is.EqualTo("玩家自称小明。"));
            Assert.That(memory.Recent.Single().Question, Is.EqualTo("叫我小明"));
            Assert.That(memory.Pending, Is.Empty);
            await store.ForgetAsync(first, "aria", CancellationToken.None).ConfigureAwait(false);
            var forgotten = await store.LoadAsync(first, "aria", CancellationToken.None).ConfigureAwait(false);
            Assert.That(forgotten.Summary, Is.Empty);
            Assert.That(forgotten.Recent, Is.Empty);
            Assert.That((await store.LoadAsync(second, "aria", CancellationToken.None).ConfigureAwait(false)).Recent, Has.Count.EqualTo(1));
            Assert.That((await store.LoadAsync(first, "other-npc", CancellationToken.None).ConfigureAwait(false)).Recent, Has.Count.EqualTo(1));
            await store.ForgetAsync(second, "aria", CancellationToken.None).ConfigureAwait(false);
            await store.ForgetAsync(first, "other-npc", CancellationToken.None).ConfigureAwait(false);
        }
    }
}
