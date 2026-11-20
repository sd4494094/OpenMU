// <copyright file="QuestItemLevelPacketTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameServer.RemoteView;
using MUnique.OpenMU.GameServer.RemoteView.Quest;
using MUnique.OpenMU.Network.Packets.ServerToClient;
using MUnique.OpenMU.Persistence.InMemory;
using B = MUnique.OpenMU.Persistence.BasicModel;

/// <summary>
/// Tests inventory counts and item previews in both quest progress formats.
/// </summary>
public class QuestItemLevelPacketTests
{
    /// <summary>
    /// Both packet formats use the configured level restriction for counting and previewing.
    /// </summary>
    /// <param name="requiredLevel">The exact level, or no restriction.</param>
    /// <param name="hasInventory">Whether the player has an inventory.</param>
    /// <param name="expectedCount">The number of matching items.</param>
    [TestCase(5, true, 1U)]
    [TestCase(0, true, 1U)]
    [TestCase(null, true, 4U)]
    [TestCase(5, false, 0U)]
    public void ProgressUsesConfiguredItemLevel(int? requiredLevel, bool hasInventory, uint expectedCount)
    {
        var map = new B.ItemDefinition { Id = Guid.NewGuid(), Group = 14, Number = 28, Durability = 1 };
        var other = new B.ItemDefinition { Id = Guid.NewGuid(), Group = 14, Number = 29, Durability = 1 };
        var configuration = new B.GameConfiguration();
        configuration.Items.Add(map);
        configuration.Items.Add(other);
        var requirement = new B.QuestItemRequirement
        {
            Item = map,
            MinimumNumber = 2,
            DropItemGroup = requiredLevel.HasValue ? new B.DropItemGroup { ItemLevel = (byte)requiredLevel.Value } : null,
        };
        Item[]? items = hasInventory
            ? [new B.Item { Definition = map, Level = 0 },
               new B.Item { Definition = map, Level = 4 },
               new B.Item { Definition = map, Level = 5 },
               new B.Item { Definition = map, Level = 6 },
               new B.Item { Definition = other, Level = 5 }]
            : null;
        using var context = new InMemoryPersistenceContextProvider().CreateNewContext();

        var standard = new QuestConditionRef(new byte[QuestConditionRef.Length]);
        var standardSerializer = new ItemSerializer();
        requirement.AssignTo(standard, items, standardSerializer);
        var standardPreview = standardSerializer.DeserializeItem(standard.RequiredItemData, configuration, context);
        Assert.That(standard.CurrentCount, Is.EqualTo(expectedCount));
        Assert.That(standard.RequiredCount, Is.EqualTo(2U));
        Assert.That(standardPreview.Definition, Is.SameAs(map));
        Assert.That(standardPreview.Level, Is.EqualTo(requiredLevel ?? 0));

        var extended = new QuestConditionExtendedRef(new byte[QuestConditionExtendedRef.Length]);
        var extendedSerializer = new ItemSerializerExtended();
        requirement.AssignTo(extended, items, extendedSerializer);
        var extendedPreview = extendedSerializer.DeserializeItem(extended.RequiredItemData, configuration, context);
        Assert.That(extended.CurrentCount, Is.EqualTo(expectedCount));
        Assert.That(extended.RequiredCount, Is.EqualTo(2U));
        Assert.That(extendedPreview.Definition, Is.SameAs(map));
        Assert.That(extendedPreview.Level, Is.EqualTo(requiredLevel ?? 0));
    }
}
