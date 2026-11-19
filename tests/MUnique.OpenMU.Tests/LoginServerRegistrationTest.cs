// <copyright file="LoginServerRegistrationTest.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Tests;

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MUnique.OpenMU.DataModel.Entities;
using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.MuHelper;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameServer;
using MUnique.OpenMU.Interfaces;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.PlugIns;
using BasicModel = MUnique.OpenMU.Persistence.BasicModel;

/// <summary>
/// Tests that the registration of a player at the login server is released, so that the account
/// isn't registered as connected after the session ended.
/// </summary>
[TestFixture]
public class LoginServerRegistrationTest
{
    private const string LoginName = "owner";
    private const string Password = "password";

    private readonly List<GameServerContext> _contexts = new();

    /// <summary>
    /// Disposes the game server contexts of the test, which stops their timers.
    /// </summary>
    /// <returns>The task.</returns>
    [TearDown]
    public async Task TearDownAsync()
    {
        foreach (var context in this._contexts)
        {
            await context.DisposeAsync().ConfigureAwait(false);
        }

        this._contexts.Clear();
    }

    /// <summary>
    /// Tests that a player which disconnects after the login server accepted its login, but before
    /// the account was assigned, still has its registration, so that it's logged off.
    /// Previously, the account was logged off by the assigned account, so it stayed registered as
    /// connected and every further login was rejected.
    /// </summary>
    [Test]
    public async ValueTask DisconnectDuringLoginReleasesTheRegistrationAsync()
    {
        Player? player = null;
        Task? disconnect = null;
        var loginServer = new Mock<ILoginServer>();
        loginServer
            .Setup(server => server.TryLoginAsync(LoginName, It.IsAny<byte>()))
            .Returns(() =>
            {
                // The connection is lost while the login server accepts the login.
                disconnect = Task.Run(() => player!.DisconnectAsync().AsTask());
                return Task.FromResult(true);
            });
        var gameContext = await this.CreateGameServerContextAsync(loginServer.Object).ConfigureAwait(false);
        player = new LoginTestPlayer(gameContext);
        await player.PlayerState.TryAdvanceToAsync(PlayerState.LoginScreen).ConfigureAwait(false);
        string? releasedAtDisconnect = null;
        player.PlayerDisconnected += disconnectedPlayer =>
        {
            releasedAtDisconnect = disconnectedPlayer.ReleaseLoginServerRegistration();
            return ValueTask.CompletedTask;
        };

        await new LoginAction().LoginAsync(player, LoginName, Password).ConfigureAwait(false);
        await disconnect!.ConfigureAwait(false);

        Assert.That(releasedAtDisconnect, Is.EqualTo(LoginName));
        Assert.That(player.ReleaseLoginServerRegistration(), Is.Null);
    }

    /// <summary>
    /// Tests that the registration is released by a failed login, so that it isn't logged off twice.
    /// </summary>
    [Test]
    public async ValueTask FailedLoginLogsOffOnceAsync()
    {
        var loginServer = new Mock<ILoginServer>();
        loginServer.Setup(server => server.TryLoginAsync(LoginName, It.IsAny<byte>())).ReturnsAsync(true);
        var gameContext = await this.CreateGameServerContextAsync(loginServer.Object).ConfigureAwait(false);
        var player = new FailingLoginPlayer(gameContext);
        await player.PlayerState.TryAdvanceToAsync(PlayerState.LoginScreen).ConfigureAwait(false);

        await new LoginAction().LoginAsync(player, LoginName, Password).ConfigureAwait(false);

        loginServer.Verify(server => server.LogOffAsync(LoginName, It.IsAny<byte>()), Times.Once);
        Assert.That(player.ReleaseLoginServerRegistration(), Is.Null);
    }

    /// <summary>
    /// Verifies that running out of Zen cannot block disconnect and that reconnect waits for the old save.
    /// </summary>
    [Test]
    public async ValueTask HeartbeatTimeoutAfterHelperRunsOutOfZenReleasesRegistrationAsync()
    {
        var loginServer = new LoginServer.LoginServer();
        var config = TestGameServers.CreateStartableConfiguration();
        config.RecoveryInterval = int.MaxValue;
        config.Maps.Add(new BasicModel.GameMapDefinition { TerrainData = new byte[ushort.MaxValue + 3] });
        var server = TestGameServers.Create(config, loginServer);
        await using var serverLifetime = server.ConfigureAwait(false);
        var context = (GameServerContext)server.Context;
        context.ServerConfiguration.Maps.Add(config.Maps.Single());
        var helperFeature = context.FeaturePlugIns.GetPlugIn<MuHelperFeaturePlugIn>();
        if (helperFeature is null)
        {
            helperFeature = new MuHelperFeaturePlugIn();
            context.FeaturePlugIns.AddPlugIn(helperFeature, true);
        }

        helperFeature.Configuration = new MuHelperConfiguration { PayInterval = TimeSpan.FromMilliseconds(20) };

        AsyncEventHandler<PlayerConnectedEventArgs>? onConnected = null;
        var listener = new Mock<IGameServerListener>();
        listener.SetupAdd(l => l.PlayerConnected += It.IsAny<AsyncEventHandler<PlayerConnectedEventArgs>>())
            .Callback<AsyncEventHandler<PlayerConnectedEventArgs>>(handler => onConnected = handler);
        server.AddListener(listener.Object);
        var player = new LoginTestPlayer(context);
        await onConnected!(new PlayerConnectedEventArgs(player)).ConfigureAwait(false);
        var character = PlayerTestHelper.CreateCharacter(context);
        await PlayerTestHelper.EnterWorldAsync(player, character).ConfigureAwait(false);
        player.Attributes![Stats.Level] = 1;
        player.Money = 20;
        Assert.That(await loginServer.TryLoginAsync(LoginName, server.Id).ConfigureAwait(false), Is.True);
        player.SetLoginServerRegistration(LoginName);

        Assert.That(await player.MuHelper.TryStartAsync().ConfigureAwait(false), Is.True);
        Assert.That(player.Money, Is.Zero);
        await WaitUntilAsync(() => player.Attributes[Stats.IsMuHelperActive] == 0).ConfigureAwait(false);

        var continueSaving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holdSaveLock = player.RunPersistenceExclusiveAsync(() => new ValueTask(continueSaving.Task)).AsTask();
        var detection = new LostConnectionDetectionPlugIn
        {
            Configuration = new LostConnectionDetectionConfiguration { Timeout = TimeSpan.FromTicks(1) },
        };
        player.ReportAlive();
        detection.ForceStart();
        var disconnect = detection.ExecuteTaskAsync(context).AsTask();
        try
        {
            await WaitUntilAsync(() => player.SelectedCharacter is null).ConfigureAwait(false);
            Assert.That(await loginServer.TryLoginAsync(LoginName, server.Id).ConfigureAwait(false), Is.False);
        }
        finally
        {
            continueSaving.SetResult();
            await holdSaveLock.ConfigureAwait(false);
        }

        await disconnect.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        Assert.That(player.PlayerState.CurrentState, Is.SameAs(PlayerState.Finished));
        Assert.That(await loginServer.TryLoginAsync(LoginName, server.Id).ConfigureAwait(false), Is.True);
    }

    private static async ValueTask WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token).ConfigureAwait(false);
        }
    }

    private async ValueTask<GameServerContext> CreateGameServerContextAsync(ILoginServer loginServer)
    {
        var persistenceContextProvider = new InMemoryPersistenceContextProvider();
        using (var context = persistenceContextProvider.CreateNewContext())
        {
            var account = context.CreateNew<Account>();
            account.LoginName = LoginName;
            account.PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password);
            await context.SaveChangesAsync().ConfigureAwait(false);
        }

        var gameServerContext = LoginTestSetup.CreateGameServerContext(persistenceContextProvider, loginServer);
        this._contexts.Add(gameServerContext);
        return gameServerContext;
    }

    /// <summary>
    /// A player whose login fails after the login server accepted it, because assigning the account fails.
    /// </summary>
    private sealed class FailingLoginPlayer : LoginTestPlayer
    {
        public FailingLoginPlayer(IGameContext gameContext)
            : base(gameContext)
        {
            this.PlayerLoggedIn += _ => throw new InvalidOperationException("Login failed.");
        }
    }
}
