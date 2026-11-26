// <copyright file="CashShopScriptDownloadTests.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Web.Tests.CashShop;

using System.IO;
using System.Net;
using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using MUnique.OpenMU.DataModel.Configuration;
using MUnique.OpenMU.DataModel.Configuration.Items;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.AdminAuth;
using MUnique.OpenMU.Persistence.InMemory;
using MUnique.OpenMU.Web.AdminPanel.API;
using MUnique.OpenMU.Web.AdminPanel.Auth;
using MUnique.OpenMU.Web.Tests.AdminAuth;

/// <summary>Exercises the real HTTP authorization and catalog download.</summary>
[TestFixture]
public sealed class CashShopScriptDownloadTests : IDisposable
{
    private WebApplication? _app;
    private HttpClient? _http;

    /// <inheritdoc />
    public void Dispose()
    {
        this._http?.Dispose();
        this._http = null;
        GC.SuppressFinalize(this);
    }

    /// <summary>Stops the test host.</summary>
    [TearDown]
    public async Task StopAsync()
    {
        this.Dispose();
        if (this._app is { } app)
        {
            await app.StopAsync().ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
            this._app = null;
        }
    }

    /// <summary>Setup mode must not expose the catalog anonymously, and other roles may not download.</summary>
    [TestCase(null, false, false)]
    [TestCase(nameof(AdminRole.Administrator), false, false)]
    [TestCase(nameof(AdminRole.Operator), true, false)]
    [TestCase(nameof(AdminRole.Viewer), true, false)]
    [TestCase("CashShop", true, false)]
    [TestCase(nameof(AdminRole.Administrator), true, true)]
    public async Task RequiresAuthenticatedAdministratorAsync(string? role, bool signIn, bool allowed)
    {
        var http = await this.StartAsync(role).ConfigureAwait(false);
        if (signIn)
        {
            using var login = await http.GetAsync("/test-login").ConfigureAwait(false);
            Assert.That(login.IsSuccessStatusCode, Is.True);
        }

        using var response = await http.GetAsync("/download/cash-shop-script").ConfigureAwait(false);
        Assert.That(response.IsSuccessStatusCode, Is.EqualTo(allowed));
        if (allowed)
        {
            Assert.That(response.Content.Headers.ContentType!.MediaType, Is.EqualTo("application/zip"));
            Assert.That(response.Headers.CacheControl!.NoStore, Is.True);
            Assert.That((await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Take(2), Is.EqualTo(new byte[] { 0x50, 0x4B }));
        }
        else
        {
            Assert.That(response.StatusCode, Is.AnyOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.Redirect));
        }
    }

    private async Task<HttpClient> StartAsync(string? role)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AdminPanel:Auth:DataProtectionKeyPath"] = Path.Combine(Path.GetTempPath(), "openmu-cashshop-script-test-keys"),
        });
        var user = new AdminUser { Id = Guid.NewGuid(), LoginName = "admin", NormalizedLoginName = "ADMIN", Roles = role ?? string.Empty, SecurityStamp = Guid.NewGuid().ToString() };
        var users = new InMemoryAdminUserRepository();
        if (role is not null)
        {
            await users.AddAsync(user).ConfigureAwait(false);
        }

        builder.Services.AddSingleton<IAdminUserRepository>(users);
        builder.Services.AddSingleton<IApiKeyRepository>(new InMemoryApiKeyRepository());
        builder.Services.AddAdminPanelAuth(builder.Configuration);
        var provider = new InMemoryPersistenceContextProvider();
        using var context = provider.CreateNewContext();
        var configuration = context.CreateNew<GameConfiguration>();
        configuration.CashShopConfiguration = context.CreateNew<CashShopConfiguration>();
        var package = context.CreateNew<CashShopPackage>();
        package.PackageSequence = 1;
        package.Name = "Jewel";
        var product = context.CreateNew<CashShopProduct>();
        product.ProductSequence = 1;
        product.PriceSequence = 1;
        product.ItemDefinition = context.CreateNew<ItemDefinition>();
        product.ItemDefinition.Name = "Jewel";
        package.Products.Add(product);
        configuration.CashShopConfiguration.Packages.Add(package);
        await context.SaveChangesAsync().ConfigureAwait(false);
        var source = new Mock<IDataSource<GameConfiguration>>();
        source.Setup(s => s.GetOwnerAsync(It.IsAny<Guid>(), It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(configuration);
        source.Setup(s => s.GetContextAsync(It.IsAny<System.Threading.CancellationToken>())).ReturnsAsync(context);
        builder.Services.AddSingleton<IPersistenceContextProvider>(provider);
        builder.Services.AddSingleton(source.Object);
        builder.Services.AddControllers().AddApplicationPart(typeof(CashShopScriptController).Assembly);
        var app = builder.Build();
        app.UseRouting();
        app.UseAdminPanelAuth();
        app.MapControllers();
        app.MapTestSignIn("/test-login", user);
        await app.StartAsync().ConfigureAwait(false);
        this._app = app;
        this._http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { BaseAddress = new Uri(app.Urls.Single()) };
        return this._http;
    }
}
