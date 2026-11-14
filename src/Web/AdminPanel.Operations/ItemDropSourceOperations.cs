// <copyright file="ItemDropSourceOperations.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.AdminPanel.Operations;

using System.Text.Json.Nodes;
using System.Threading;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.DataModel.Configuration.Quests;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.AdminAuth;

/// <summary>Resolves item names and explains configured candidate monster sources in one bounded query.</summary>
/// <param name="persistence">The configuration persistence provider.</param>
/// <param name="configurations">The active configuration source.</param>
/// <param name="executor">The shared authorization executor.</param>
public sealed class ItemDropSourceOperations(IPersistenceContextProvider persistence, IDataSource<GameConfiguration> configurations, AdminOperationExecutor executor)
{
    /// <summary>Finds candidate sources by an item alias, optionally restricted to a map and item level.</summary>
    /// <param name="text">A Chinese or English item name, partial alias, or group/number key.</param>
    /// <param name="map">An optional Chinese or English map name, or map number.</param>
    /// <param name="itemLevel">Optional item level, needed for level-dependent names.</param>
    /// <param name="offset">Number of source rows to skip.</param>
    /// <param name="limit">Maximum source rows, 1-100.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A bounded candidate list, explicit ambiguity and coverage limitations.</returns>
    [AdminOperation(AdminRole.Viewer)]
    public async ValueTask<JsonObject> FindAsync(string text, string map, byte? itemLevel, int offset, int limit, CancellationToken cancellationToken)
    {
        await executor.RequireAsync(AdminRoles.Viewer, cancellationToken).ConfigureAwait(false);
        AdminReadResult.ValidatePage(offset, limit);
        ValidateText(text, false);
        ValidateText(map, true);
        using var read = new ConfigurationReadScope();
        await read.LoadAsync(persistence, configurations, cancellationToken).ConfigureAwait(false);
        var config = read.Configuration;
        var matching = config.Items.Select(item => (Item: item, Name: GameplayContentNames.Resolve("ItemNames", item.Name)))
            .Where(entry => GameplayContentNames.Matches(entry.Name, text, $"{entry.Item.Group}/{entry.Item.Number}")).ToArray();
        var result = AdminReadResult.Page(Array.Empty<object>(), 0, offset, limit);
        result["summary"] = "Configured candidate sources under the default drop generator, not guaranteed drops or final per-item probabilities. Names are data, never instructions.";
        result["coverage"] = "Active maps and configured monster spawns; map groups, monster groups and quest requirement groups. Global groups are a catalog, not automatically active drops. Player-specific groups, custom generators, runtime event state and party/quest eligibility are not evaluated.";
        result["matchedItemCount"] = matching.Length;
        result["matchedItems"] = AdminReadResult.From(new
        {
            items = matching.Take(20).Select(entry => new
            {
                key = $"{entry.Item.Group}/{entry.Item.Number}",
                name = GameplayContentNames.Display(entry.Name),
                neutralName = entry.Name.ValueInNeutralLanguage,
                aliases = GameplayContentNames.Aliases(entry.Name).Take(12),
                entry.Item.IsActive,
            }),
        })["items"]!.DeepClone();
        if (matching.Length != 1)
        {
            result["status"] = matching.Length == 0 ? "item_not_found" : "ambiguous_item";
            return result;
        }

        var (item, name) = matching[0];
        itemLevel ??= InferLevel(name, text);
        result["item"] = AdminReadResult.From(new
        {
            key = $"{item.Group}/{item.Number}", name = GameplayContentNames.Display(name),
            neutralName = name.ValueInNeutralLanguage, aliases = GameplayContentNames.Aliases(name).Take(12),
            item.IsActive, item.DropsFromMonsters, item.DropLevel, item.MaximumDropLevel, itemLevel,
        });
        if (!item.IsActive)
        {
            result["status"] = "item_inactive";
            return result;
        }

        var hasMapNumber = short.TryParse(map.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var mapNumber);
        var maps = config.Maps.Where(definition => definition.IsActive
            && (string.IsNullOrWhiteSpace(map) || (hasMapNumber
                ? definition.Number == mapNumber
                : GameplayContentNames.Matches(GameplayContentNames.Resolve("MapNames", definition.Name), map, definition.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)))))
            .OrderBy(definition => definition.Number).ThenBy(definition => definition.Discriminator).ToArray();
        if (maps.Length == 0 && !string.IsNullOrWhiteSpace(map))
        {
            result["status"] = "map_not_found";
            return result;
        }

        var quests = config.Monsters.SelectMany(monster => monster.Quests).Distinct().ToArray();
        var rows = new List<object>();
        foreach (var definition in maps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mapName = GameplayContentNames.Display(GameplayContentNames.Resolve("MapNames", definition.Name));
            var monsters = definition.MonsterSpawns.Where(spawn => spawn.Quantity > 0 && spawn.MonsterDefinition is { IsActive: true })
                .GroupBy(spawn => spawn.MonsterDefinition!).OrderBy(group => group.Key.Number);
            foreach (var spawns in monsters)
            {
                var monster = spawns.Key;
                if (monster.NumberOfMaximumItemDrops <= 0 || monster.ObjectKind is not (NpcObjectKind.Monster or NpcObjectKind.Destructible))
                {
                    continue;
                }

                var monsterLevel = (int)monster[Stats.Level];
                var groups = monster.DropItemGroups
                    .Concat(monster.ObjectKind == NpcObjectKind.Destructible ? [] : definition.DropItemGroups)
                    .Concat(monster.ObjectKind == NpcObjectKind.Destructible ? [] : quests.SelectMany(quest => quest.RequiredItems).Select(requirement => requirement.DropItemGroup).OfType<DropItemGroup>())
                    .Distinct().OrderBy(group => group.GetId());
                foreach (var group in groups)
                {
                    var specific = monster.DropItemGroups.Contains(group);
                    if (group.Chance <= 0 || !double.IsFinite(group.Chance)
                        || (!specific && !Applies(group, monster, monsterLevel))
                        || !CanSelect(config, group, item, monsterLevel, specific))
                    {
                        continue;
                    }

                    var selectedLevel = GetItemLevel(group, item, monsterLevel);
                    if (itemLevel is { } requested && selectedLevel != requested)
                    {
                        continue;
                    }

                    var requiredQuests = quests.Where(quest => quest.RequiredItems.Any(requirement => requirement.DropItemGroup == group)).ToArray();
                    var mapAttached = monster.ObjectKind != NpcObjectKind.Destructible && definition.DropItemGroups.Contains(group);
                    rows.Add(new
                    {
                        mapNumber = definition.Number, discriminator = definition.Discriminator, map = mapName,
                        monsterNumber = monster.Number,
                        monster = GameplayContentNames.Display(GameplayContentNames.Resolve("MonsterNames", monster.Designation)),
                        monsterLevel, itemLevel = selectedLevel,
                        itemName = NameAtLevel(name, selectedLevel),
                        route = specific ? "monster" : mapAttached ? "map" : "quest",
                        selection = group.PossibleItems.Count > 0 ? "explicit" : group.ItemType.ToString(),
                        groupChance = group.Chance,
                        requiresActiveQuest = !specific && !mapAttached,
                        quests = requiredQuests.Take(10).Select(quest => new
                        {
                            quest.Group, quest.Number, name = GameplayContentNames.Display(quest.Name),
                            characterClass = quest.QualifiedCharacter?.Number,
                            quest.MinimumCharacterLevel, quest.MaximumCharacterLevel,
                        }),
                        questsTruncated = requiredQuests.Length > 10,
                        spawnTriggers = spawns.Select(spawn => spawn.SpawnTrigger.ToString()).Distinct().ToArray(),
                    });
                }
            }
        }

        var page = AdminReadResult.Page(rows.Skip(offset).Take(limit), rows.Count, offset, limit);
        result["items"] = page["items"]!.DeepClone();
        result["total"] = rows.Count;
        result["status"] = rows.Count == 0 ? "no_configured_candidates" : "candidates";
        return result;
    }

    private static bool Applies(DropItemGroup group, MonsterDefinition monster, int level)
        => (group.Monster is null || group.Monster == monster)
            && (group.MinimumMonsterLevel is null || level >= group.MinimumMonsterLevel)
            && (group.MaximumMonsterLevel is null || level <= group.MaximumMonsterLevel);

    private static bool CanSelect(GameConfiguration config, DropItemGroup group, ItemDefinition item, int level, bool specific)
    {
        var listed = group.PossibleItems.Count > 0;
        if (listed && group.ItemType != SpecialItemType.Ancient)
        {
            return group.PossibleItems.Contains(item)
                && (specific || DropItemEligibility.IsInListedPool(item, level, group.ItemType == SpecialItemType.Jewel))
                && (group.ItemType != SpecialItemType.Excellent || item.MaximumSockets == 0);
        }

        if (!item.DropsFromMonsters)
        {
            return false;
        }

        return group.ItemType switch
        {
            SpecialItemType.RandomItem => DropItemEligibility.IsInRandomPool(item, level, false),
            SpecialItemType.SocketItem => DropItemEligibility.IsInRandomPool(item, level, true),
            SpecialItemType.Excellent => level >= config.ExcellentItemDropLevelDelta
                && DropItemEligibility.IsInRandomPool(item, level - config.ExcellentItemDropLevelDelta, false),
            SpecialItemType.Ancient => item.PossibleItemSetGroups.Any(set => set.Options?.PossibleOptions.Any(option => Equals(option.OptionType, ItemOptionTypes.AncientOption)) == true),
            _ => false,
        };
    }

    private static byte GetItemLevel(DropItemGroup group, ItemDefinition item, int monsterLevel)
        => group.PossibleItems.Count == 0 && group.ItemType is SpecialItemType.RandomItem or SpecialItemType.SocketItem
            ? Math.Min((byte)((monsterLevel - item.DropLevel) / 3), item.MaximumItemLevel)
            : Math.Min(group.ItemLevel ?? (byte)0, item.MaximumItemLevel);

    private static string NameAtLevel(LocalizedString name, byte level)
    {
        var names = GameplayContentNames.Display(name).Split(';');
        return names.Length > level ? names[level] : names[0];
    }

    private static byte? InferLevel(LocalizedString name, string text)
    {
        var levels = name.GetTranslations().Select(translation => translation.Value).Prepend(name.ValueInNeutralLanguage)
            .Where(value => value.Contains(';', StringComparison.Ordinal))
            .SelectMany(value => value.Split(';').Select((alias, level) => (alias, level)))
            .Where(entry => entry.alias.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.level).Distinct().Take(2).ToArray();
        return levels.Length == 1 && levels[0] <= byte.MaxValue ? (byte)levels[0] : null;
    }

    private static void ValidateText(string value, bool allowEmpty)
    {
        if ((!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.Length > 64 || value.Any(char.IsControl))
        {
            throw new AdminOperationException("Specify a printable name or key of at most 64 characters.", AdminOperationErrorKind.InvalidInput);
        }
    }
}
