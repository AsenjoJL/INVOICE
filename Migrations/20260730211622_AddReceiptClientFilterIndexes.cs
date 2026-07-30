using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HazelInvoice.Migrations
{
    /// <inheritdoc />
    [Migration("20260730211622_AddReceiptClientFilterIndexes")]
    public partial class AddReceiptClientFilterIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            CreateIndexConcurrently(migrationBuilder, "IX_Receipts_PaidAmount", "Receipts", "\"PaidAmount\"");
            CreateIndexConcurrently(migrationBuilder, "IX_Receipts_CustomerId_Date", "Receipts", "\"CustomerId\", \"Date\"");
            CreateIndexConcurrently(migrationBuilder, "IX_Receipts_Date_Id", "Receipts", "\"Date\", \"Id\"");
            CreateIndexConcurrently(migrationBuilder, "IX_Customers_GroupName", "Customers", "\"GroupName\"");
            CreateIndexConcurrently(migrationBuilder, "IX_Customers_Name", "Customers", "\"Name\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            DropIndexConcurrently(migrationBuilder, "IX_Customers_Name");
            DropIndexConcurrently(migrationBuilder, "IX_Customers_GroupName");
            DropIndexConcurrently(migrationBuilder, "IX_Receipts_Date_Id");
            DropIndexConcurrently(migrationBuilder, "IX_Receipts_CustomerId_Date");
            DropIndexConcurrently(migrationBuilder, "IX_Receipts_PaidAmount");
        }

        private static void CreateIndexConcurrently(
            MigrationBuilder migrationBuilder,
            string indexName,
            string tableName,
            string columns)
        {
            migrationBuilder.Sql(
                $"""CREATE INDEX CONCURRENTLY IF NOT EXISTS "{indexName}" ON "{tableName}" ({columns});""",
                suppressTransaction: true);
        }

        private static void DropIndexConcurrently(MigrationBuilder migrationBuilder, string indexName)
        {
            migrationBuilder.Sql(
                $"""DROP INDEX CONCURRENTLY IF EXISTS "{indexName}";""",
                suppressTransaction: true);
        }
    }
}
