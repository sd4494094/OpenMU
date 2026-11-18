// <copyright file="MemoryRepositoryConcurrencyTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using System.Threading;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.InMemory;

/// <summary>
/// Tests shared in-memory repository reads and saves while another context adds objects.
/// </summary>
[TestFixture]
public class MemoryRepositoryConcurrencyTest
{
    /// <summary>
    /// A save must not enumerate a pending-object list while another context modifies it.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async ValueTask AddingWhileAnotherContextSavesDoesNotCorruptTheSaveAsync()
    {
        var repository = new MemoryRepository<Entity>();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new Entity { Id = Guid.NewGuid() };
        var second = new Entity { Id = Guid.NewGuid() };
        repository.Add(first.Id, first);
        first.ReadingId = () =>
        {
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("The save was not released.");
            }
        };
        var save = Task.Run(repository.OnSaveChanges);
        Task add = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            add = Task.Run(() =>
            {
                adding.TrySetResult();
                repository.Add(second.Id, second);
            });
            await adding.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            // A synchronized add waits for this save. An unprotected add can finish
            // and invalidate the save's pending-object enumerator before it resumes.
            await Task.WhenAny(add, Task.Delay(250)).ConfigureAwait(false);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(save, add).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        Assert.That(await repository.GetAllAsync().ConfigureAwait(false), Is.EquivalentTo(new[] { first, second }));
    }

    /// <summary>
    /// A returned query must remain enumerable when another context inserts an object.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async ValueTask QueriesReturnStableSnapshotsAsync()
    {
        var repository = new MemoryRepository<Entity>();
        var first = new Entity { Id = Guid.NewGuid() };
        var second = new Entity { Id = Guid.NewGuid() };
        repository.Add(first.Id, first);
        var result = await repository.GetAllAsync().ConfigureAwait(false);
        using var iterator = result.GetEnumerator();
        Assert.That(iterator.MoveNext(), Is.True);
        Assert.That(iterator.Current, Is.SameAs(first));
        repository.Add(second.Id, second);
        Assert.That(iterator.MoveNext(), Is.False);
        Assert.That(await repository.GetAllAsync().ConfigureAwait(false), Is.EquivalentTo(new[] { first, second }));
    }

    private sealed class Entity : IIdentifiable
    {
        private Guid _id;

        public Action? ReadingId { get; set; }

        public Guid Id
        {
            get
            {
                this.ReadingId?.Invoke();
                return this._id;
            }

            set => this._id = value;
        }
    }
}
