// <copyright file="BotPopulationOperations.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel.Operations;

using System.Text.Json.Nodes;
using System.Threading;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Bots;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.AdminAuth;
using MUnique.OpenMU.PlugIns;

/// <summary>Previews a bot configuration against persisted names and actual startup observations.</summary>
/// <param name="persistence">The persistence provider.</param>
/// <param name="configurations">The configuration source.</param>
/// <param name="servers">The running server provider.</param>
public sealed class BotPopulationOperations(IPersistenceContextProvider persistence, IDataSource<GameConfiguration> configurations, IServerProvider servers)
{
    /// <summary>Reads quantities and inventory for a proposed configuration; never generates, deletes or restarts bots.</summary>
    /// <param name="numberOfAccounts">The proposed total account target, or null to use the saved value.</param>
    /// <param name="charactersPerAccount">The proposed slots per account, or null to use the saved value.</param>
    /// <param name="characterNames">The proposed inventory text, or null to use the saved inventory.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A read-only preview with aggregate quantities and process-local startup observations.</returns>
    [AdminOperation(AdminRole.Administrator)]
    public async ValueTask<JsonObject> PreviewAsync(int? numberOfAccounts, int? charactersPerAccount, string? characterNames, CancellationToken cancellationToken)
    {
        var game = await configurations.GetOwnerAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var stored = game.PlugInConfigurations.FirstOrDefault(plugin => plugin.TypeId == typeof(BotFeaturePlugIn).GUID)?.GetConfiguration<BotConfiguration>(null) ?? new BotConfiguration();
        var plan = new BotConfiguration
        {
            Enabled = stored.Enabled,
            StartAsFreshCharacters = stored.StartAsFreshCharacters,
            NumberOfAccounts = numberOfAccounts ?? stored.NumberOfAccounts,
            MaxCharactersPerAccount = charactersPerAccount ?? stored.MaxCharactersPerAccount,
            CharacterNames = characterNames ?? stored.CharacterNames,
        };
        if (plan.NumberOfAccounts is < 0 or > 1000 || plan.MaxCharactersPerAccount is < 1 or > BotConfiguration.MaxCharactersPerAccountLimit || plan.CharacterNames?.Length > 65536)
        {
            throw new ArgumentException("Bot account target must be 0..1000, characters per account 1..5, and inventory text at most 65536 characters.");
        }

        var errors = new List<string>();
        IReadOnlyList<string> names = [];
        try
        {
            names = plan.GetValidatedCharacterNames();
        }
        catch (InvalidOperationException)
        {
            errors.Add("invalidInventory");
        }

        using var context = persistence.CreateNewPlayerContext(game);
        var accounts = await context.GetBotAccountSummariesAsync(BotAccountNaming.Prefix, cancellationToken).ConfigureAwait(false);
        var byLogin = accounts.ToDictionary(account => account.LoginName, StringComparer.Ordinal);
        var perAccount = Math.Clamp(Math.Min(plan.MaxCharactersPerAccount, game.MaximumCharactersPerAccount), 1, BotConfiguration.MaxCharactersPerAccountLimit);
        var newAccounts = 0;
        var existingTargetCharacters = 0;
        var missingExistingSlots = 0;
        for (var index = 1; index <= plan.NumberOfAccounts; index++)
        {
            if (!byLogin.TryGetValue(BotAccountNaming.GetLoginName(index), out var account))
            {
                newAccounts++;
                continue;
            }

            if (!account.IsBot)
            {
                errors.Add("loginConflict");
                continue;
            }

            existingTargetCharacters += account.Characters.Count(pair => pair.Key < perAccount);
            missingExistingSlots += Enumerable.Range(0, perAccount).Count(slot => !account.Characters.ContainsKey((byte)slot));
        }

        var occupied = await context.GetAccountIdsByCharacterNamesAsync(names, cancellationToken).ConfigureAwait(false);
        var available = names.Where(name => !occupied.ContainsKey(name)).ToArray();
        var newCharacters = newAccounts * perAccount;
        if (newCharacters > available.Length)
        {
            errors.Add("insufficientInventory");
        }

        var bots = accounts.Where(account => account.IsBot).ToArray();
        var plugins = servers.Servers.OfType<IGameServerContextProvider>()
            .Select(server => server.Context.FeaturePlugIns.GetPlugIn<BotFeaturePlugIn>()).OfType<BotFeaturePlugIn>().Distinct();
        var runtime = plugins.SelectMany(plugin => plugin.GetPopulationStatus(plan)).GroupBy(status => status.ServerId).Select(group => group.First()).ToArray();
        return AdminReadResult.From(new
        {
            readOnly = true, canSave = errors.Count == 0, errors = errors.Distinct().ToArray(),
            targetAccounts = plan.NumberOfAccounts, charactersPerAccount = perAccount,
            targetCharacters = plan.NumberOfAccounts * perAccount,
            existingAccounts = bots.Length, existingCharacters = bots.Sum(account => account.Characters.Count),
            existingChineseNames = bots.Sum(account => account.Characters.Values.Count(name => name.Any(character => character is >= '\u4e00' and <= '\u9fff'))),
            newAccounts, newCharacters, expectedSelectedCharacters = existingTargetCharacters + newCharacters,
            missingExistingSlots, nextAccountIndex = accounts.Where(account => account.IsBot).Select(account => BotAccountNaming.GetIndex(account.LoginName)).DefaultIfEmpty().Max() + 1,
            inventoryTotal = names.Count, inventoryAvailable = available.Length,
            inventoryChinese = available.Count(name => name.Any(character => character is >= '\u4e00' and <= '\u9fff')),
            inventoryEnglish = available.Count(name => name.All(character => character <= 127)),
            usesDefaultInventory = plan.ParseCharacterNames().Count == 0,
            enabled = stored.Enabled, destructiveRequested = stored.ResetBots || stored.PurgeBots,
            requiresStartup = true, runtime,
        });
    }
}
