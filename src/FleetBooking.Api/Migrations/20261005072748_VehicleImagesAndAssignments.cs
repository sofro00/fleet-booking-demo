using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FleetBooking.Api.Migrations
{
    /// <inheritdoc />
    public partial class VehicleImagesAndAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AssignedUserId",
                table: "Vehicles",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "ImageContent",
                table: "Vehicles",
                type: "varbinary(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageContentType",
                table: "Vehicles",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ImageUpdatedUtc",
                table: "Vehicles",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Vehicles_AssignedUserId",
                table: "Vehicles",
                column: "AssignedUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Vehicles_Users_AssignedUserId",
                table: "Vehicles",
                column: "AssignedUserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Vehicles_Users_AssignedUserId",
                table: "Vehicles");

            migrationBuilder.DropIndex(
                name: "IX_Vehicles_AssignedUserId",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "AssignedUserId",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "ImageContent",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "ImageContentType",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "ImageUpdatedUtc",
                table: "Vehicles");
        }
    }
}
