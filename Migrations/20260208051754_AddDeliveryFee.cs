using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HazelInvoice.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryFee : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "DeliveryFee",
                table: "WeeklyPrices",
                type: "numeric(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DeliveryFee",
                table: "Products",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.Sql(
                "UPDATE \"WeeklyPrices\" " +
                "SET \"DeliveryFee\" = CASE " +
                "WHEN \"DeliveryPrice\" <> \"BasePrice\" THEN (\"DeliveryPrice\" - \"BasePrice\") " +
                "ELSE NULL END");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeliveryFee",
                table: "WeeklyPrices");

            migrationBuilder.DropColumn(
                name: "DeliveryFee",
                table: "Products");
        }
    }
}
