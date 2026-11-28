using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MUnique.OpenMU.Persistence.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class RecoverCastleSiegeSettlement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "BattleStartUtc",
                schema: "data",
                table: "CastleSiegePendingReward",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDelivered",
                schema: "data",
                table: "CastleSiegePendingReward",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "CastleSiegeGuildScoreAward",
                schema: "guild",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GuildId = table.Column<Guid>(type: "uuid", nullable: false),
                    BattleStartUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Score = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CastleSiegeGuildScoreAward", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CastleSiegePendingReward_CharacterId_BattleStartUtc",
                schema: "data",
                table: "CastleSiegePendingReward",
                columns: new[] { "CharacterId", "BattleStartUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CastleSiegeGuildScoreAward_GuildId_BattleStartUtc",
                schema: "guild",
                table: "CastleSiegeGuildScoreAward",
                columns: new[] { "GuildId", "BattleStartUtc" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The previous version would otherwise deliver these completed rewards again.
            migrationBuilder.Sql("""
                DELETE FROM data."CastleSiegePendingReward" WHERE "IsDelivered";
                """);

            migrationBuilder.DropTable(
                name: "CastleSiegeGuildScoreAward",
                schema: "guild");

            migrationBuilder.DropIndex(
                name: "IX_CastleSiegePendingReward_CharacterId_BattleStartUtc",
                schema: "data",
                table: "CastleSiegePendingReward");

            migrationBuilder.DropColumn(
                name: "BattleStartUtc",
                schema: "data",
                table: "CastleSiegePendingReward");

            migrationBuilder.DropColumn(
                name: "IsDelivered",
                schema: "data",
                table: "CastleSiegePendingReward");
        }
    }
}
