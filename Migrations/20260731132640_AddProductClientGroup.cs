using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HazelInvoice.Migrations
{
    /// <inheritdoc />
    public partial class AddProductClientGroup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClientGroupId",
                table: "Products",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_ClientGroupId",
                table: "Products",
                column: "ClientGroupId");

            migrationBuilder.AddForeignKey(
                name: "FK_Products_ClientGroups_ClientGroupId",
                table: "Products",
                column: "ClientGroupId",
                principalTable: "ClientGroups",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Products_ClientGroups_ClientGroupId",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_ClientGroupId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ClientGroupId",
                table: "Products");
        }
    }
}
