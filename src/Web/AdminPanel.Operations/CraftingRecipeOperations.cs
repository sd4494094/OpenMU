// <copyright file="CraftingRecipeOperations.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel.Operations;

using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.ItemCrafting;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.AdminAuth;

/// <summary>Looks up configured crafting recipes by name or output item.</summary>
/// <param name="persistence">The configuration persistence provider.</param>
/// <param name="configurations">The active configuration source.</param>
/// <param name="executor">The shared authorization executor.</param>
public sealed class CraftingRecipeOperations(IPersistenceContextProvider persistence, IDataSource<GameConfiguration> configurations, AdminOperationExecutor executor)
{
    private const int DetailLimit = 20;

    /// <summary>Lists matching recipes attached to active NPCs.</summary>
    /// <param name="text">The recipe name, output name or output item group/number.</param>
    /// <param name="offset">The number of matches to skip.</param>
    /// <param name="limit">The page size.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The material rules, success settings, costs and result pool.</returns>
    [AdminOperation(AdminRole.Viewer)]
    public async ValueTask<JsonObject> FindAsync(string text, int offset, int limit, CancellationToken cancellationToken)
    {
        await executor.RequireAsync(AdminRoles.Viewer, cancellationToken).ConfigureAwait(false);
        AdminReadResult.ValidatePage(offset, limit);
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64 || text.Any(char.IsControl))
        {
            throw new AdminOperationException("Enter a recipe or output item name of 1-64 characters.", AdminOperationErrorKind.InvalidInput);
        }

        using var read = new ConfigurationReadScope();
        await read.LoadAsync(persistence, configurations, cancellationToken).ConfigureAwait(false);
        var matches = read.Configuration.Monsters.Where(npc => npc.IsActive)
            .SelectMany(npc => npc.ItemCraftings.Select(recipe => (Npc: npc, Recipe: recipe, Name: GameplayContentNames.Resolve("CraftingNames", recipe.Name))))
            .Where(entry => GameplayContentNames.Matches(entry.Name, text, entry.Recipe.Number.ToString(CultureInfo.InvariantCulture))
                || entry.Recipe.SimpleCraftingSettings?.ResultItems.Any(output => output.ItemDefinition is { } item
                    && GameplayContentNames.Matches(GameplayContentNames.Resolve("ItemNames", item.Name), text, $"{item.Group}/{item.Number}")) == true)
            .OrderBy(entry => entry.Recipe.Number).ThenBy(entry => entry.Npc.Number).ToArray();
        var rows = matches.Skip(offset).Take(limit).Select(entry => new
        {
            recipeNumber = entry.Recipe.Number,
            recipeName = GameplayContentNames.Display(entry.Name),
            npcName = GameplayContentNames.Display(GameplayContentNames.Resolve("MonsterNames", entry.Npc.Designation)),
            settings = Describe(entry.Recipe.SimpleCraftingSettings),
        });
        var result = AdminReadResult.Page(rows, matches.Length, offset, limit);
        result["status"] = matches.Length == 0 ? "no_configured_recipe" : "configured";
        result["coverage"] = "Configured NPC recipes. Null settings mean the handler controls the recipe. A concrete success rate depends on the supplied materials.";
        return result;
    }

    private static object? Describe(SimpleCraftingSettings? settings)
        => settings is null ? null : new
        {
            baseSuccessPercent = settings.SuccessPercent,
            maximumSuccessPercent = settings.MaximumSuccessPercent == 0 ? (int?)null : settings.MaximumSuccessPercent,
            baseZen = settings.Money,
            zenPerSuccessPercentagePoint = settings.MoneyPerFinalSuccessPercentage,
            settings.NpcPriceDivisor,
            settings.MultipleAllowed,
            resultSelection = settings.ResultItemSelect.ToString(),
            requirements = settings.RequiredItems.OrderByDescending(rule => rule.MinimumAmount).ThenByDescending(rule => rule.MinimumItemLevel)
                .Take(DetailLimit).Select(rule => new
                {
                    rule.MinimumAmount,
                    maximumAmount = rule.MaximumAmount == 0 ? (int?)null : rule.MaximumAmount,
                    rule.MinimumItemLevel,
                    rule.MaximumItemLevel,
                    requiredOptions = rule.RequiredItemOptions.Take(DetailLimit).Select(option => GameplayContentNames.Display(option.Name)),
                    anyMatchingItem = rule.PossibleItems.Count == 0,
                    possibleItems = rule.PossibleItems.OrderBy(item => item.Group).ThenBy(item => item.Number).Take(DetailLimit).Select(DescribeItem),
                    rule.AddPercentage,
                    rule.NpcPriceDivisor,
                    onFailure = rule.FailResult.ToString(),
                    onSuccess = rule.SuccessResult.ToString(),
                    rule.Reference,
                }),
            outputs = settings.ResultItems.Take(DetailLimit).Select(output => new
            {
                item = output.ItemDefinition is { } item ? DescribeItem(item) : null,
                output.Reference,
                output.AddLevel,
                output.RandomMinimumLevel,
                output.RandomMaximumLevel,
            }),
            detailsTruncated = settings.RequiredItems.Count > DetailLimit || settings.ResultItems.Count > DetailLimit
                || settings.RequiredItems.Any(rule => rule.PossibleItems.Count > DetailLimit || rule.RequiredItemOptions.Count > DetailLimit),
        };

    private static object DescribeItem(ItemDefinition item) => new
    {
        key = $"{item.Group}/{item.Number}",
        name = GameplayContentNames.Display(GameplayContentNames.Resolve("ItemNames", item.Name)),
        item.IsActive,
    };
}
