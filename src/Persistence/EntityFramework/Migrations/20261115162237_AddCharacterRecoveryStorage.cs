using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MUnique.OpenMU.Persistence.EntityFramework.Migrations
{
    /// <inheritdoc />
    public partial class AddCharacterRecoveryStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "RecoveryStorageId",
                schema: "data",
                table: "Character",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Character_RecoveryStorageId",
                schema: "data",
                table: "Character",
                column: "RecoveryStorageId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Character_ItemStorage_RecoveryStorageId",
                schema: "data",
                table: "Character",
                column: "RecoveryStorageId",
                principalSchema: "data",
                principalTable: "ItemStorage",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // An older server cannot recover these results. Require the owner to reclaim
            // them before removing the reference which makes them reachable.
            migrationBuilder.Sql(@"
DO $$ BEGIN
    IF EXISTS (
        SELECT 1 FROM data.""Character"" c
        JOIN data.""Item"" i ON i.""ItemStorageId"" = c.""RecoveryStorageId""
    ) THEN
        RAISE EXCEPTION 'Recover retained character items before downgrading.';
    END IF;
END $$;
");

            migrationBuilder.DropForeignKey(
                name: "FK_Character_ItemStorage_RecoveryStorageId",
                schema: "data",
                table: "Character");

            migrationBuilder.DropIndex(
                name: "IX_Character_RecoveryStorageId",
                schema: "data",
                table: "Character");

            migrationBuilder.DropColumn(
                name: "RecoveryStorageId",
                schema: "data",
                table: "Character");
        }
    }
}
