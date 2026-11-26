using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MUnique.OpenMU.Persistence.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class AddGroundItemDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "GroundDeliveryId",
                schema: "data",
                table: "Item",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GroundItemDelivery",
                schema: "data",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HostServerId = table.Column<Guid>(type: "uuid", nullable: false),
                    MapDefinitionId = table.Column<Guid>(type: "uuid", nullable: false),
                    MapInstanceId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerCharacterId = table.Column<Guid>(type: "uuid", nullable: false),
                    PositionX = table.Column<byte>(type: "smallint", nullable: false),
                    PositionY = table.Column<byte>(type: "smallint", nullable: false),
                    DroppedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OwnerPriorityUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroundItemDelivery", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Item_GroundDeliveryId",
                schema: "data",
                table: "Item",
                column: "GroundDeliveryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroundItemDelivery_HostServerId_ExpiresAt",
                schema: "data",
                table: "GroundItemDelivery",
                columns: new[] { "HostServerId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GroundItemDelivery_HostServerId_MapDefinitionId_MapInstance~",
                schema: "data",
                table: "GroundItemDelivery",
                columns: new[] { "HostServerId", "MapDefinitionId", "MapInstanceId", "ExpiresAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_Item_GroundItemDelivery_GroundDeliveryId",
                schema: "data",
                table: "Item",
                column: "GroundDeliveryId",
                principalSchema: "data",
                principalTable: "GroundItemDelivery",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Keep both the check and the following DDL in the migration transaction.
            // The table lock prevents a new delivery appearing after an empty check.
            migrationBuilder.Sql(@"
LOCK TABLE data.""GroundItemDelivery"" IN ACCESS EXCLUSIVE MODE;
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM data.""GroundItemDelivery"") THEN
        RAISE EXCEPTION 'Consume durable ground deliveries before downgrading.';
    END IF;
END $$;
");

            migrationBuilder.DropForeignKey(
                name: "FK_Item_GroundItemDelivery_GroundDeliveryId",
                schema: "data",
                table: "Item");

            migrationBuilder.DropTable(
                name: "GroundItemDelivery",
                schema: "data");

            migrationBuilder.DropIndex(
                name: "IX_Item_GroundDeliveryId",
                schema: "data",
                table: "Item");

            migrationBuilder.DropColumn(
                name: "GroundDeliveryId",
                schema: "data",
                table: "Item");
        }
    }
}
