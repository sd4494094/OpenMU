// <copyright file="GroundItemQueryBuilder.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.Persistence.EntityFramework.Json;

using Microsoft.EntityFrameworkCore.Metadata;

/// <summary>
/// Selects a bounded delivery page before constructing any complete item aggregates.
/// </summary>
internal sealed class GroundItemQueryBuilder : JsonQueryBuilder
{
    /// <inheritdoc />
    protected override string GetRootSource(IEntityType entityType) => """
        (
            SELECT item.*
            FROM data."GroundItemDelivery" delivery
            JOIN data."Item" item ON item."GroundDeliveryId" = delivery."Id"
            WHERE delivery."HostServerId" = @host
              AND (@map IS NULL OR delivery."MapDefinitionId" = @map)
              AND (@allInstances OR delivery."MapInstanceId" IS NOT DISTINCT FROM @instance)
              AND (@expiresAfter IS NULL OR delivery."ExpiresAt" > @expiresAfter)
              AND (@expiresBefore IS NULL OR delivery."ExpiresAt" <= @expiresBefore)
              AND (@cursorExpires IS NULL OR (delivery."ExpiresAt", delivery."Id") > (@cursorExpires, @cursorId))
              AND item."ItemStorageId" IS NULL
            ORDER BY delivery."ExpiresAt", delivery."Id"
            LIMIT @take
        )
        """;
}
