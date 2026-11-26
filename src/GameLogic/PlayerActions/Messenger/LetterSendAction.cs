// <copyright file="LetterSendAction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.Messenger;

using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.GameLogic.Views.Inventory;
using MUnique.OpenMU.GameLogic.Views.Messenger;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence;

/// <summary>
/// Action to send a letter to another player.
/// </summary>
public class LetterSendAction
{
    /// <summary>
    /// Sends the letter.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="receiver">The receiver.</param>
    /// <param name="message">The message.</param>
    /// <param name="title">The title.</param>
    /// <param name="rotation">The rotation.</param>
    /// <param name="animation">The animation.</param>
    /// <param name="letterId">The client side letter id.</param>
    public ValueTask SendLetterAsync(Player player, string receiver, string message, string title, byte rotation, byte animation, uint letterId)
        => player.RunPersistenceExclusiveAsync(() => this.SendLetterCoreAsync(player, receiver, message, title, rotation, animation, letterId));

    private static ValueTask SendResultAsync(Player player, LetterSendSuccess result, uint letterId)
        => player.InvokeViewPlugInAsync<ILetterSendResultPlugIn>(p => p.LetterSendResultAsync(result, letterId));

    private async ValueTask SendLetterCoreAsync(Player player, string receiver, string message, string title, byte rotation, byte animation, uint letterId)
    {
        using var loggerScope = player.Logger.BeginScope(this.GetType());
        var sendPrice = player.GameContext.Configuration.LetterSendPrice;
        if (player.Money < sendPrice)
        {
            await player.ShowLocalizedBlueMessageAsync(nameof(PlayerMessage.NotEnoughMoneyToSendLetter)).ConfigureAwait(false);
            await SendResultAsync(player, LetterSendSuccess.NotEnoughMoney, letterId).ConfigureAwait(false);
            return;
        }

        if (!player.IsConnected || player.IsTemplatePlayer || player.SelectedCharacter is not { Inventory: { } inventory } character)
        {
            await SendResultAsync(player, LetterSendSuccess.TryAgain, letterId).ConfigureAwait(false);
            return;
        }

        var context = player.PersistenceContext;
        var added = new List<object>();
        var originalMoney = inventory.Money;
        var saving = false;
        var result = LetterSendSuccess.TryAgain;
        LetterHeader? letter = null;
        try
        {
            letter = context.CreateNew<LetterHeader>();
            added.Add(letter);
            letter.LetterDate = DateTime.UtcNow;
            letter.SenderName = character.Name;
            letter.ReceiverName = receiver;
            letter.Subject = title;
            if (await context.CanSaveLetterAsync(letter).ConfigureAwait(false))
            {
                var body = context.CreateNew<LetterBody>();
                added.Add(body);
                body.Header = letter;
                body.Message = message;
                var appearance = context.CreateNew<AppearanceData>();
                added.Add(appearance);
                body.SenderAppearance = appearance;
                appearance.CharacterClass = player.AppearanceData.CharacterClass;
                foreach (var equipped in player.AppearanceData.EquippedItems)
                {
                    var item = context.CreateNew<ItemAppearance>();
                    added.Add(item);
                    item.ItemSlot = equipped.ItemSlot;
                    item.Definition = equipped.Definition;
                    item.Level = equipped.Level;
                    foreach (var option in equipped.VisibleOptions.Distinct())
                    {
                        item.VisibleOptions.Add(player.GameContext.Configuration.ItemOptionTypes.First(type => type.Equals(option)));
                    }

                    appearance.EquippedItems.Add(item);
                }

                body.Rotation = rotation;
                body.Animation = animation;
                inventory.Money = checked(originalMoney - sendPrice);
                saving = true;
                if (await player.SaveProgressAsync().ConfigureAwait(false))
                {
                    result = LetterSendSuccess.Success;
                }
            }
            else
            {
                result = LetterSendSuccess.ReceiverNotExists;
            }
        }
        catch (Exception ex) when (!saving || ex is PersistenceConflictException)
        {
            player.Logger.LogError(ex, "Couldn't save the letter from {Player}.", player);
        }
        catch (Exception ex)
        {
            // A lost acknowledgement can leave the database committed. End the
            // session with the original tracked outcome so the balance and letter
            // stay paired even if teardown retries the save.
            player.Logger.LogError(ex, "Couldn't confirm the letter commit for {Player}; disconnecting.", player);
            await player.DisconnectAsync().ConfigureAwait(false);
            return;
        }

        if (letter is null || result != LetterSendSuccess.Success)
        {
            inventory.Money = originalMoney;
            for (var i = added.Count - 1; i >= 0; i--)
            {
                context.Detach(added[i]);
            }

            await SendResultAsync(player, result, letterId).ConfigureAwait(false);
            if (result == LetterSendSuccess.TryAgain)
            {
                await player.ShowLocalizedBlueMessageAsync(nameof(PlayerMessage.ErrorDuringSendingLetter)).ConfigureAwait(false);
            }

            return;
        }

        await SendResultAsync(player, LetterSendSuccess.Success, letterId).ConfigureAwait(false);
        if (sendPrice != 0)
        {
            await player.InvokeViewPlugInAsync<IUpdateMoneyPlugIn>(p => p.UpdateMoneyAsync()).ConfigureAwait(false);
        }

        // EF can populate the sender's inbox automatically when the sender is also the receiver.
        // The ordinary forwarding path only adds letters the receiving context hasn't seen yet.
        var index = character.Letters?.IndexOf(letter) ?? -1;
        if (index >= 0)
        {
            await player.InvokeViewPlugInAsync<IAddToLetterListPlugIn>(p => p.AddToLetterListAsync(letter, (ushort)index, true)).ConfigureAwait(false);
        }

        // Try to forward it to the player, if he is online
        if (player.GameContext is IGameServerContext gameServerContext)
        {
            await gameServerContext.FriendServer.ForwardLetterAsync(letter).ConfigureAwait(false);

            // E.g. the Discord bot can notify the receiver.
            await gameServerContext.EventPublisher.GameEventAsync(new LetterReceivedEvent(gameServerContext.Id, DateTime.UtcNow, letter.ReceiverName ?? receiver, letter.SenderName ?? player.Name, letter.Subject ?? string.Empty)).ConfigureAwait(false);
        }
    }
}
