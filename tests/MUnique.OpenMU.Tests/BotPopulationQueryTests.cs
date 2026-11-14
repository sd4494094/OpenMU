// <copyright file="BotPopulationQueryTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Microsoft.EntityFrameworkCore;
using MUnique.OpenMU.Persistence.EntityFramework;

/// <summary>Checks the bot preview against the real compiled PostgreSQL model without connecting to a database.</summary>
[TestFixture]
public sealed class BotPopulationQueryTests
{
    /// <summary>The mapped relationship translates to SQL without loading account credentials or character inventories.</summary>
    [Test]
    public void BotCharacterProjectionTranslatesWithTheProductionModel()
    {
        using var context = new EntityDataContext();
        var sql = PlayerContext.CreateBotAccountCharactersQuery(
            context.Set<Persistence.EntityFramework.Model.Account>().AsNoTracking(),
            "bot").ToQueryString();
        Assert.That(sql, Does.Contain("JOIN"));
        Assert.That(sql, Does.Contain("CharacterSlot"));
        Assert.That(sql, Does.Contain("IsBot"));
        Assert.That(sql, Does.Not.Contain("Password"));
        Assert.That(sql, Does.Not.Contain("ItemStorage"));
    }
}
