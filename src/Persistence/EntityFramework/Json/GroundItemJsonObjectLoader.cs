// <copyright file="GroundItemJsonObjectLoader.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.EntityFramework.Json;

using System.Data;
using System.Data.Common;
using System.IO;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MUnique.OpenMU.Persistence.EntityFramework.Model;

/// <summary>
/// Reads one statement snapshot of detached item aggregates with real cached definitions.
/// </summary>
internal sealed class GroundItemJsonObjectLoader
{
    private static readonly Lazy<string> Query = new(() => "SET LOCAL jit = off;\n"
        + new GroundItemQueryBuilder().BuildJsonQueryForEntity(EntityDataContext.CompleteModel.FindEntityType(typeof(Item))!)
        + " ORDER BY (result.\"RawGroundDelivery\"->>'ExpiresAt')::timestamptz, result.\"GroundDeliveryId\";");

    private readonly ConfigurationIdReferenceResolver _configuration;

    /// <summary>
    /// Initializes a new instance of the <see cref="GroundItemJsonObjectLoader"/> class.
    /// </summary>
    /// <param name="configuration">One warmed configuration cache generation.</param>
    public GroundItemJsonObjectLoader(ConfigurationIdReferenceResolver configuration)
    {
        this._configuration = configuration;
    }

    /// <summary>
    /// Reads a bounded page without attaching its results. The connection must be open.
    /// </summary>
    /// <param name="context">The account context executing the statement.</param>
    /// <param name="query">The validated page filter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The detached page.</returns>
    public async ValueTask<IReadOnlyList<DataModel.Entities.Item>> LoadAsync(DbContext context, GroundItemQuery query, CancellationToken cancellationToken)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = Query.Value;
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
        AddParameter(command, "host", DbType.Guid, query.HostServerId);
        AddParameter(command, "map", DbType.Guid, query.MapDefinitionId);
        AddParameter(command, "instance", DbType.Guid, query.MapInstanceId);
        AddParameter(command, "allInstances", DbType.Boolean, query.IncludeAllInstances);
        AddParameter(command, "expiresAfter", DbType.DateTime, query.ExpiresAfterUtc);
        AddParameter(command, "expiresBefore", DbType.DateTime, query.ExpiresBeforeUtc);
        AddParameter(command, "cursorExpires", DbType.DateTime, query.AfterExpiresAtUtc);
        AddParameter(command, "cursorId", DbType.Guid, query.AfterDeliveryId);
        AddParameter(command, "take", DbType.Int32, query.Limit);
        var items = new List<DataModel.Entities.Item>();
        var deserializer = new JsonObjectDeserializer();
        var references = new CachingReferenceHandler(this._configuration);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.SingleResult, cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            await using var json = reader.GetStream(2);
            cancellationToken.ThrowIfCancellationRequested();
            var item = deserializer.Deserialize<Item>(json, references)
                ?? throw new InvalidDataException("A recovery item aggregate could not be deserialized.");
            this.ValidateReferences(item);
            items.Add(item);
        }

        return items;
    }

    private static DateTime RequireUtc(DateTime timestamp)
    {
        if (timestamp.Kind == DateTimeKind.Unspecified)
        {
            throw new InvalidDataException("A recovery deadline has no time zone.");
        }

        return timestamp.ToUniversalTime();
    }

    private static void AddParameter(DbCommand command, string name, DbType type, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private void ValidateReferences(Item item)
    {
        if (item.GroundDelivery is not { } delivery || item.GroundDeliveryId != delivery.GetId())
        {
            throw new InvalidDataException("A recovery item has no matching delivery generation.");
        }

        // PostgreSQL writes timestamptz with an offset. System.Text.Json materializes
        // an offset DateTime in the process's local zone; normalize the instant before
        // comparing deadlines, reattaching it to Npgsql or using it as a UTC cursor.
        delivery.DroppedAt = RequireUtc(delivery.DroppedAt);
        delivery.OwnerPriorityUntil = RequireUtc(delivery.OwnerPriorityUntil);
        delivery.ExpiresAt = RequireUtc(delivery.ExpiresAt);

        this.RequireCached(item.Definition);
        foreach (var option in item.ItemOptions)
        {
            this.RequireCached(option.ItemOption);
        }

        foreach (var set in item.ItemSetGroups)
        {
            this.RequireCached(set);
        }

        if (item.StorePriceItemDefinition is { } priceDefinition)
        {
            this.RequireCached(priceDefinition);
        }
    }

    private void RequireCached(object? definition)
    {
        // The normal reference converter can create an id-only placeholder on a cache
        // miss. Never publish such an incomplete definition as an actual ground item.
        if (definition is not IIdentifiable identified || identified.Id == Guid.Empty
            || !ReferenceEquals(definition, this._configuration.ResolveReference(identified.Id.ToString())))
        {
            throw new InvalidDataException("A recovery item references an unavailable game configuration object.");
        }
    }
}
