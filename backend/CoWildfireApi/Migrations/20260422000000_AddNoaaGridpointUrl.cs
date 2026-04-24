using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CoWildfireApi.Migrations
{
    /// <inheritdoc />
    public partial class AddNoaaGridpointUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "noaa_gridpoint_url",
                table: "h3_cells",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "noaa_gridpoint_url",
                table: "h3_cells");
        }
    }
}
