using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace KeepBill.Data.Migrations
{
    public partial class AddCustomers : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(nullable: false),
                    Name = table.Column<string>(maxLength: 120, nullable: false),
                    TaxId = table.Column<string>(maxLength: 20, nullable: true),
                    Email = table.Column<string>(maxLength: 160, nullable: true),
                    Phone = table.Column<string>(maxLength: 40, nullable: true),
                    BillingAddress = table.Column<string>(maxLength: 180, nullable: true),
                    City = table.Column<string>(maxLength: 90, nullable: true),
                    Country = table.Column<string>(maxLength: 90, nullable: true),
                    Notes = table.Column<string>(maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(nullable: false, defaultValueSql: "timezone('utc', now())"),
                    UpdatedAt = table.Column<DateTime>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Customers_Name",
                table: "Customers",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TaxId",
                table: "Customers",
                column: "TaxId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Customers");
        }
    }
}
