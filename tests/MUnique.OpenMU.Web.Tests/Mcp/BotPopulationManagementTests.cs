// <copyright file="BotPopulationManagementTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Tests.Mcp;

using System.ComponentModel.DataAnnotations;
using System.Threading;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;
using MUnique.OpenMU.Web.AdminPanel.Operations;
using MUnique.OpenMU.Web.Shared.Components.Form;
using MUnique.OpenMU.Web.Shared.Services;

/// <summary>Exercises population previews and the real configuration form without changing running game worlds.</summary>
[TestFixture]
public sealed class BotPopulationManagementTests
{
    private InMemoryPersistenceContextProvider _persistence = null!;
    private IContext _context = null!;
    private GameConfiguration _game = null!;
    private BotPopulationOperations _operations = null!;

    /// <summary>Creates two existing accounts with five characters each.</summary>
    [SetUp]
    public async Task SetUpAsync()
    {
        this._persistence = new InMemoryPersistenceContextProvider();
        this._context = this._persistence.CreateNewContext();
        this._game = this._context.CreateNew<GameConfiguration>();
        this._game.MaximumCharactersPerAccount = 5;
        var plugin = this._context.CreateNew<PlugInConfiguration>();
        plugin.TypeId = typeof(BotFeaturePlugIn).GUID;
        plugin.IsActive = true;
        plugin.SetConfiguration(new BotConfiguration { Enabled = true, NumberOfAccounts = 2, MaxCharactersPerAccount = 5 }, null);
        this._game.PlugInConfigurations.Add(plugin);
        this.AddAccount("bot0001", true, "Old1");
        this.AddAccount("bot0002", true, "Old2");
        await this._context.SaveChangesAsync().ConfigureAwait(false);
        var source = new Mock<IDataSource<GameConfiguration>>();
        source.Setup(item => item.GetOwnerAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(this._game);
        var servers = new Mock<IServerProvider>();
        servers.SetupGet(item => item.Servers).Returns(new List<IManageableServer>());
        this._operations = new BotPopulationOperations(this._persistence, source.Object, servers.Object);
    }

    /// <summary>Releases the isolated fixture.</summary>
    [TearDown]
    public void TearDown() => this._context.Dispose();

    /// <summary>A larger account target predicts only missing accounts and never creates them.</summary>
    [Test]
    public async Task ExpansionIsAccurateAndReadOnlyAsync()
    {
        var result = await this._operations.PreviewAsync(4, 5, null, default).ConfigureAwait(false);
        Assert.That(result["newAccounts"]!.GetValue<int>(), Is.EqualTo(2));
        Assert.That(result["newCharacters"]!.GetValue<int>(), Is.EqualTo(10));
        Assert.That(result["existingCharacters"]!.GetValue<int>(), Is.EqualTo(10));
        Assert.That(result["inventoryAvailable"]!.GetValue<int>(), Is.EqualTo(500));
        Assert.That(result["canSave"]!.GetValue<bool>(), Is.True);
        using var player = this._persistence.CreateNewPlayerContext(this._game);
        Assert.That(await player.GetAccountByLoginNameAsync("bot0003").ConfigureAwait(false), Is.Null);
        Assert.That((await player.GetBotAccountSummariesAsync(BotAccountNaming.Prefix).ConfigureAwait(false)), Has.Count.EqualTo(2));
    }

    /// <summary>Reducing the target does not predict deletion or silently backfill existing character slots.</summary>
    [Test]
    public async Task ExistingSlotsAndReducedTargetsAreReportedHonestlyAsync()
    {
        using var player = this._persistence.CreateNewPlayerContext(this._game);
        var account = (await player.GetAccountByLoginNameAsync("bot0001").ConfigureAwait(false))!;
        foreach (var character in account.Characters.Skip(1).ToArray())
        {
            account.Characters.Remove(character);
        }

        var result = await this._operations.PreviewAsync(1, 5, null, default).ConfigureAwait(false);
        Assert.That(result["newCharacters"]!.GetValue<int>(), Is.Zero);
        Assert.That(result["missingExistingSlots"]!.GetValue<int>(), Is.EqualTo(4));
        Assert.That(result["targetCharacters"]!.GetValue<int>(), Is.EqualTo(5));
        Assert.That(result["expectedSelectedCharacters"]!.GetValue<int>(), Is.EqualTo(1));
        Assert.That(result["existingCharacters"]!.GetValue<int>(), Is.EqualTo(6));
    }

    /// <summary>Actual player-owned names reduce inventory capacity and non-bot login conflicts block the plan.</summary>
    [Test]
    public async Task OccupiedNamesAndLoginConflictsAreDetectedAsync()
    {
        var account = this.AddAccount("bot0003", false, "Human");
        account.Characters.First().Name = "阿川";
        await this._context.SaveChangesAsync().ConfigureAwait(false);
        var result = await this._operations.PreviewAsync(3, 5, null, default).ConfigureAwait(false);
        Assert.That(result["inventoryAvailable"]!.GetValue<int>(), Is.EqualTo(499));
        Assert.That(result["canSave"]!.GetValue<bool>(), Is.False);
        Assert.That(result["errors"]!.ToJsonString(), Does.Contain("loginConflict"));
        Assert.That(result["existingAccounts"]!.GetValue<int>(), Is.EqualTo(2));
    }

    /// <summary>Bad inventories and insufficient inventories never produce a misleading valid preview.</summary>
    [TestCase("River,river", "invalidInventory")]
    [TestCase("手有点冷", "invalidInventory")]
    [TestCase("阿川,River", "insufficientInventory")]
    public async Task InvalidAndInsufficientInventoryBlocksOrdinarySaveAsync(string names, string error)
    {
        var result = await this._operations.PreviewAsync(3, 5, names, default).ConfigureAwait(false);
        Assert.That(result["canSave"]!.GetValue<bool>(), Is.False);
        Assert.That(result["errors"]!.ToJsonString(), Does.Contain(error));
    }

    /// <summary>The shared configuration validation also protects API writes against invalid complete names.</summary>
    [Test]
    public void TypedConfigurationRejectsInvalidInventory()
    {
        var configuration = new BotConfiguration { CharacterNames = "River,river" };
        var errors = new List<ValidationResult>();
        Assert.That(Validator.TryValidateObject(configuration, new ValidationContext(configuration), errors, true), Is.False);
        Assert.That(errors.SelectMany(error => error.MemberNames), Does.Contain(nameof(BotConfiguration.CharacterNames)));
    }

    /// <summary>The calculator changes the draft target, refreshes the new-character count, and never saves by itself.</summary>
    [Test]
    public void FormCalculatorAndSubmitUseThePreview()
    {
        using var ui = this.CreateUi();
        var draft = new BotConfiguration { Enabled = true, NumberOfAccounts = 2, MaxCharactersPerAccount = 5 };
        var saves = 0;
        var form = ui.Render<BotConfigurationForm>(parameters => parameters
            .Add(component => component.Configuration, draft)
            .Add(component => component.PersistenceContext, this._context)
            .Add(component => component.OnValidSubmit, () => saves++));
        form.WaitForAssertion(() => Assert.That(form.Find("[data-value=newCharacters]").TextContent, Is.EqualTo("0")));
        form.Find("#AdditionalBotCharacters").Change("10");
        form.Find("[data-testid=bot-calculate]").Click();
        form.WaitForAssertion(() => Assert.That(form.Find("[data-value=newCharacters]").TextContent, Is.EqualTo("10")));
        Assert.That(draft.NumberOfAccounts, Is.EqualTo(4));
        Assert.That(saves, Is.Zero);
        form.Find("form").Submit();
        form.WaitForAssertion(() => Assert.That(saves, Is.EqualTo(1)));
    }

    /// <summary>A shortage blocks submission, and destructive controls require an explicit in-form acknowledgement.</summary>
    [Test]
    public void FormRejectsShortageAndDoesNotSilentlyApproveDeletion()
    {
        using var ui = this.CreateUi();
        var draft = new BotConfiguration { Enabled = true, NumberOfAccounts = 3, MaxCharactersPerAccount = 5, CharacterNames = "阿川" };
        var saves = 0;
        var form = ui.Render<BotConfigurationForm>(parameters => parameters
            .Add(component => component.Configuration, draft)
            .Add(component => component.PersistenceContext, this._context)
            .Add(component => component.OnValidSubmit, () => saves++));
        form.WaitForAssertion(() => Assert.That(form.Markup, Does.Contain("not enough unused reviewed names")));
        form.Find("form").Submit();
        Assert.That(saves, Is.Zero);
        form.Find("#ResetBots").Change(true);
        form.WaitForAssertion(() => Assert.That(form.Find("[data-testid=bot-save]").HasAttribute("disabled"), Is.True));
        Assert.That(saves, Is.Zero);
    }

    private BunitContext CreateUi()
    {
        var ui = new BunitContext();
        ui.Services.AddLogging();
        ui.Services.AddSingleton(this._operations);
        ui.Services.AddSingleton(Mock.Of<IChangeNotificationService>());
        return ui;
    }

    private Account AddAccount(string login, bool isBot, string prefix)
    {
        var account = this._context.CreateNew<Account>();
        account.LoginName = login;
        account.IsBot = isBot;
        for (byte slot = 0; slot < 5; slot++)
        {
            var character = this._context.CreateNew<Character>();
            character.CharacterSlot = slot;
            character.Name = $"{prefix}{slot}";
            account.Characters.Add(character);
        }

        return account;
    }
}
