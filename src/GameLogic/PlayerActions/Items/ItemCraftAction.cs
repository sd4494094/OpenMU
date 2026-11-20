// <copyright file="ItemCraftAction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.Items;

using System.Collections.Concurrent;
using MUnique.OpenMU.DataModel.Configuration.ItemCrafting;
using MUnique.OpenMU.GameLogic.Views.NPC;

/// <summary>
/// The action to craft items with crafting NPCs.
/// </summary>
public class ItemCraftAction
{
    private readonly ConcurrentDictionary<ItemCrafting, IItemCraftingHandler> _craftingHandlerCache = new();

    /// <summary>
    /// Mixes the items at the currently opened crafting NPC.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="mixTypeId">The mix type identifier.</param>
    /// <param name="socketSlot">The socket slot.</param>
    public ValueTask MixItemsAsync(Player player, byte mixTypeId, byte socketSlot)
        => player.RunPersistenceExclusiveAsync(() => this.MixItemsCoreAsync(player, mixTypeId, socketSlot));

    /// <summary>
    /// Finds the relevant <see cref="ItemCrafting"/> by testing the mix items against every crafting's item requirements.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <returns>The relevant <see cref="ItemCrafting"/>.</returns>
    public ItemCrafting? FindAppropriateCraftingByItems(Player player)
    {
        // Wing crafting has one more requirement than chaos weapon crafting, so check it first.
        return player.OpenedNpc?.Definition.ItemCraftings
            .OrderByDescending(c => c.Number)
            .FirstOrDefault(c => this.MatchesItems(player, c));
    }

    /// <summary>
    /// Shows the crafting which matches the items of the crafting dialog, with its success rate and price,
    /// if the client of the player supports it.
    /// </summary>
    /// <param name="player">The player.</param>
    public async ValueTask ShowCraftingPreviewAsync(Player player)
    {
        if (player.ViewPlugIns.GetPlugIn<IShowItemCraftingPreviewPlugIn>() is not { } previewPlugIn)
        {
            return;
        }

        if (this.FindAppropriateCraftingByItems(player) is { } crafting
            && this.GetCraftingHandler(crafting) is BaseItemCraftingHandler craftingHandler
            && craftingHandler.GetRequiredItemsAndPrice(player) is (null, _, var successRate, var price))
        {
            await previewPlugIn.ShowCraftingPreviewAsync(crafting, successRate, price).ConfigureAwait(false);
            return;
        }

        await previewPlugIn.ShowCraftingPreviewAsync(null, 0, 0).ConfigureAwait(false);
    }

    private async ValueTask MixItemsCoreAsync(Player player, byte mixTypeId, byte socketSlot)
    {
        if (player.PlayerState.CurrentState != PlayerState.NpcDialogOpened)
        {
            return;
        }

        var npcStats = player.OpenedNpc?.Definition;

        var craftings = npcStats?.ItemCraftings.Where(c => c.Number == mixTypeId).ToList();
        if (craftings is not { Count: > 0 })
        {
            await player.InvokeViewPlugInAsync<IShowItemCraftingResultPlugIn>(p => p.ShowResultAsync(CraftingResult.IncorrectMixItems, null)).ConfigureAwait(false);
            return;
        }

        var crafting = craftings[0];
        if (craftings.Count > 1)
        {
            // Some recipes share a request number. When no variant matches, keep
            // the first handler's original validation result.
            crafting = craftings.FirstOrDefault(c => this.MatchesItems(player, c)) ?? crafting;
        }

        var craftingHandler = this.GetCraftingHandler(crafting);

        (CraftingResult, Item?) result;
        try
        {
            result = await craftingHandler.DoMixAsync(player, socketSlot).ConfigureAwait(false);
        }
        catch
        {
            result = (CraftingResult.LackingMixItems, null);
        }

        try
        {
            if (!await player.SaveProgressAsync().ConfigureAwait(false))
            {
                throw new InvalidOperationException("The crafting outcome was not saved.");
            }
        }
        catch (Exception exception)
        {
            // A failed acknowledgement of the commit may be ambiguous. Keep the same
            // outcome for the ordinary disconnect save instead of restoring ingredients
            // and allowing another roll. Teardown stays inside the persistence gate so
            // queued actions cannot use an unconfirmed result. Reconnect loads the actual
            // durable state. This also covers a failed roll which consumed materials.
            player.Logger.LogError(exception, "Couldn't confirm saving the crafting outcome for player {player}; disconnecting.", player);
            await player.DisconnectAsync().ConfigureAwait(false);
            return;
        }

        var itemList = player.TemporaryStorage?.Items.ToList() ?? new List<Item>();
        await player.InvokeViewPlugInAsync<IShowItemCraftingResultPlugIn>(p => p.ShowResultAsync(result.Item1, itemList.Count > 1 ? null : result.Item2)).ConfigureAwait(false);
        if (npcStats!.NpcWindow != NpcWindow.CombineLuckyItem)
        {
            // The lucky item dialog gets its result item with the crafting result. The item list would put the
            // items into the chaos machine dialog of the client instead.
            await player.InvokeViewPlugInAsync<IShowMerchantStoreItemListPlugIn>(
                p => p.ShowMerchantStoreItemListAsync(
                    itemList,
                    npcStats.NpcWindow == NpcWindow.PetTrainer && result.Item1 != CraftingResult.Success ? StoreKind.ResurrectionFailed : StoreKind.ChaosMachine))
                .ConfigureAwait(false);
        }

        await this.ShowCraftingPreviewAsync(player).ConfigureAwait(false);
    }

    private bool MatchesItems(Player player, ItemCrafting crafting)
        => this.GetCraftingHandler(crafting).TryGetRequiredItems(player, out _, out _) is null;

    private IItemCraftingHandler GetCraftingHandler(ItemCrafting crafting)
    {
        // This action may be shared between players, e.g. by the message handlers, so the cache must be thread-safe.
        return this._craftingHandlerCache.GetOrAdd(crafting, this.CreateCraftingHandler);
    }

    private IItemCraftingHandler CreateCraftingHandler(ItemCrafting crafting)
    {
        if (!string.IsNullOrWhiteSpace(crafting.ItemCraftingHandlerClassName))
        {
            if (ItemCraftingHandlerFactory.Create(crafting.ItemCraftingHandlerClassName, crafting.SimpleCraftingSettings) is { } handler)
            {
                return handler;
            }

            // Not a handler of this assembly, e.g. an assembly qualified type name of another assembly.
            var type = Type.GetType(crafting.ItemCraftingHandlerClassName);
            if (type != null)
            {
                if (type.BaseType == typeof(SimpleItemCraftingHandler))
                {
                    return (IItemCraftingHandler)Activator.CreateInstance(type, crafting.SimpleCraftingSettings)!;
                }

                return (IItemCraftingHandler)Activator.CreateInstance(type)!;
            }

            throw new ArgumentException($"Item crafting handler '{crafting.ItemCraftingHandlerClassName}' not found.", nameof(crafting));
        }

        if (crafting.SimpleCraftingSettings != null)
        {
            return new SimpleItemCraftingHandler(crafting.SimpleCraftingSettings);
        }

        throw new ArgumentException("No simple crafting settings or item crafting handler name specified.", nameof(crafting));
    }
}
