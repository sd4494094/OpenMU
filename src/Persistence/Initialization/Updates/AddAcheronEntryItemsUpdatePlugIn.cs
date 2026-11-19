// <copyright file="AddAcheronEntryItemsUpdatePlugIn.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.Initialization.Updates;

using System.Runtime.InteropServices;
using MUnique.OpenMU.Persistence.Initialization.VersionSeasonSix.Items;
using MUnique.OpenMU.PlugIns;

/// <summary>
/// Adds the two Acheron entry item definitions to an existing Season 6 configuration.
/// </summary>
[PlugIn]
[Display(Name = UpdateName, Description = UpdateDescription)]
[Guid("44162BBD-E1F5-44AD-809E-5B87400CDEAD")]
public sealed class AddAcheronEntryItemsUpdatePlugIn : UpdatePlugInBase
{
    private const string UpdateName = "Acheron entry items";
    private const string UpdateDescription = "Adds the Spirit Map and its fragments for the MuEx client.";

    /// <inheritdoc />
    public override string DataInitializationKey => VersionSeasonSix.DataInitialization.Id;

    /// <inheritdoc />
    public override string Name => UpdateName;

    /// <inheritdoc />
    public override string Description => UpdateDescription;

    /// <inheritdoc />
    public override DateTime CreatedAt => new(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc);

    /// <inheritdoc />
    public override bool IsMandatory => false;

    /// <inheritdoc />
    protected override ValueTask ApplyAsync(IContext context, GameConfiguration gameConfiguration)
    {
        new AcheronEntryItems(context, gameConfiguration).Initialize();
        return ValueTask.CompletedTask;
    }
}
