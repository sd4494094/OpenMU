// <copyright file="AriaServiceRegistration.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Startup.Aria;

using System.Net.Http;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MUnique.OpenMU.GameLogic.Aria;
using MUnique.OpenMU.Persistence;
using MUnique.OpenMU.Persistence.Aria;
using MUnique.OpenMU.Persistence.EntityFramework.Aria;

/// <summary>Registers Aria only when the host explicitly opts in.</summary>
public static class AriaServiceRegistration
{
    /// <summary>Adds the private Aria integration to the all-in-one host.</summary>
    /// <param name="services">The application services.</param>
    /// <param name="configuration">The host configuration.</param>
    public static void AddAria(IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Aria").Get<AriaChatOptions>() ?? new AriaChatOptions();
        if (!options.Enabled)
        {
            return;
        }

        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new ArgumentException("Aria endpoint must be an absolute HTTP(S) URL without embedded credentials.");
        }

        services.AddSingleton(options);
        services.AddSingleton<IAriaMemoryStore>(provider =>
        {
            // Initialize the game's configured connection provider before creating Aria's own small pool.
            _ = provider.GetRequiredService<IPersistenceContextProvider>();
            return PostgresAriaMemoryStore.ForGameDatabase();
        });
        services.AddHttpClient<IAriaModelClient, AriaModelClient>(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<AriaChatService>();
        services.AddSingleton<IAriaChatService>(provider => provider.GetRequiredService<AriaChatService>());
        services.AddHostedService(provider => provider.GetRequiredService<AriaChatService>());
    }
}
