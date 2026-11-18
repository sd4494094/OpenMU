// <copyright file="GroundItemDeliveryModelTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Microsoft.EntityFrameworkCore;
using MUnique.OpenMU.Persistence.EntityFramework;
using StoredDelivery = MUnique.OpenMU.Persistence.EntityFramework.Model.GroundItemDelivery;
using StoredItem = MUnique.OpenMU.Persistence.EntityFramework.Model.Item;

/// <summary>
/// Checks that live contexts enforce optional per-item delivery generation concurrency.
/// </summary>
[TestFixture]
public class GroundItemDeliveryModelTests
{
    /// <summary>
    /// Every item-saving model must retain the concurrency guard and safe delete direction.
    /// </summary>
    /// <param name="contextKind">The entity, player or trade context.</param>
    [TestCase("entity")]
    [TestCase("player")]
    [TestCase("trade")]
    public void DeliveryGenerationIsOptionalAndGuarded(string contextKind)
    {
        using EntityDataContext context = contextKind switch
        {
            "player" => new AccountContext(),
            "trade" => new TradeContext(),
            _ => new EntityDataContext(),
        };
        var item = context.Model.FindEntityType(typeof(StoredItem))!;
        var generation = item.FindProperty(nameof(StoredItem.GroundDeliveryId))!;
        Assert.That(generation.IsNullable, Is.True);
        Assert.That(generation.IsConcurrencyToken, Is.True);
        var reference = item.FindNavigation(nameof(StoredItem.RawGroundDelivery))!;
        Assert.That(reference.IsOnDependent, Is.True);
        Assert.That(reference.ForeignKey.IsUnique, Is.True);
        Assert.That(reference.ForeignKey.IsRequired, Is.False);
        Assert.That(reference.ForeignKey.DeleteBehavior, Is.EqualTo(DeleteBehavior.Restrict));
        Assert.That(reference.TargetEntityType.ClrType, Is.EqualTo(typeof(StoredDelivery)));
        Assert.That(reference.ForeignKey.Properties.Select(property => property.Name), Is.EqualTo(new[] { nameof(StoredItem.GroundDeliveryId) }));
    }
}
