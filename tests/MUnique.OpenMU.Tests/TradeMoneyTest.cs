// <copyright file="TradeMoneyTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.PlayerActions.Trade;
using MUnique.OpenMU.GameLogic.Views.Trade;

/// <summary>
/// Tests money reservation and replacement during a trade.
/// </summary>
[TestFixture]
public class TradeMoneyTest
{
    /// <summary>
    /// Replaces an offer using both the remaining inventory balance and the reserved amount.
    /// </summary>
    /// <param name="firstOffer">The initial offer.</param>
    /// <param name="replacement">The replacement offer.</param>
    /// <returns>The task.</returns>
    [TestCase(60u, 70u)]
    [TestCase(60u, 100u)]
    [TestCase(100u, 0u)]
    [TestCase(60u, 60u)]
    public async ValueTask ReplacingAnOfferUsesTheReservedBalanceAsync(uint firstOffer, uint replacement)
    {
        var (player, partner) = await CreateTradeAsync().ConfigureAwait(false);
        var action = new TradeMoneyAction();
        await action.TradeMoneyAsync(player, firstOffer).ConfigureAwait(false);
        await action.TradeMoneyAsync(player, replacement).ConfigureAwait(false);

        Assert.That(player.Money, Is.EqualTo(100 - (int)replacement));
        Assert.That(player.TradingMoney, Is.EqualTo((int)replacement));
        Assert.That(player.Money + player.TradingMoney + partner.Money, Is.EqualTo(200));

        await new TradeCancelAction().CancelTradeAsync(player).ConfigureAwait(false);
        Assert.That(player.Money, Is.EqualTo(100));
        Assert.That(partner.Money, Is.EqualTo(100));
        Assert.That(player.TradingMoney, Is.Zero);
    }

    /// <summary>
    /// Rejects amounts above the total balance without changing an existing reservation.
    /// </summary>
    /// <param name="replacement">The invalid amount.</param>
    /// <returns>The task.</returns>
    [TestCase(101u)]
    [TestCase(2147483648u)]
    [TestCase(uint.MaxValue)]
    public async ValueTask InvalidReplacementPreservesTheExistingOfferAsync(uint replacement)
    {
        var (player, partner) = await CreateTradeAsync().ConfigureAwait(false);
        var action = new TradeMoneyAction();
        await action.TradeMoneyAsync(player, 60).ConfigureAwait(false);
        await action.TradeMoneyAsync(player, replacement).ConfigureAwait(false);

        Assert.That(player.Money, Is.EqualTo(40));
        Assert.That(player.TradingMoney, Is.EqualTo(60));
        Assert.That(partner.Money, Is.EqualTo(100));

        await new TradeCancelAction().CancelTradeAsync(player).ConfigureAwait(false);
        Assert.That(player.Money, Is.EqualTo(100));
    }

    /// <summary>
    /// Keeps a confirmed offer unchanged until its owner unchecks the confirmation button.
    /// </summary>
    /// <returns>The task.</returns>
    [Test]
    public async ValueTask ConfirmedOfferCannotBeReplacedAsync()
    {
        var (player, _) = await CreateTradeAsync().ConfigureAwait(false);
        var action = new TradeMoneyAction();
        await action.TradeMoneyAsync(player, 60).ConfigureAwait(false);
        var buttonAction = new TradeButtonAction();
        await buttonAction.TradeButtonChangedAsync(player, TradeButtonState.Checked).ConfigureAwait(false);
        await action.TradeMoneyAsync(player, 70).ConfigureAwait(false);

        Assert.That(player.Money, Is.EqualTo(40));
        Assert.That(player.TradingMoney, Is.EqualTo(60));

        await buttonAction.TradeButtonChangedAsync(player, TradeButtonState.Unchecked).ConfigureAwait(false);
        await action.TradeMoneyAsync(player, 70).ConfigureAwait(false);
        Assert.That(player.Money, Is.EqualTo(30));
        Assert.That(player.TradingMoney, Is.EqualTo(70));
    }

    private static async ValueTask<(Player Player, Player Partner)> CreateTradeAsync()
    {
        var player = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        var partner = await PlayerTestHelper.CreatePlayerAsync().ConfigureAwait(false);
        player.GameContext.Configuration.MaximumInventoryMoney = 100;
        partner.GameContext.Configuration.MaximumInventoryMoney = 100;
        player.Money = 100;
        partner.Money = 100;
        await new TradeRequestAction().RequestTradeAsync(player, partner).ConfigureAwait(false);
        await new TradeAcceptAction().HandleTradeAcceptAsync(partner, true).ConfigureAwait(false);
        return (player, partner);
    }
}
