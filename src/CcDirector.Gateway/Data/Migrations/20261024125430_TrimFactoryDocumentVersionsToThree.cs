using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CcDirector.Gateway.Data.Migrations
{
    /// <inheritdoc />
    public partial class TrimFactoryDocumentVersionsToThree : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // THE NEWEST THREE VERSIONS OF EACH WORKING DOCUMENT ARE KEPT (owner, 2026-10-24: "We only need to keep the
            // last three versions."). From now on every write removes the older ones; this removes the ones already
            // held, once. Nothing else is touched.
            migrationBuilder.Sql(
                "DELETE FROM factory_document_versions AS d WHERE d.\"Version\" <= " +
                "(SELECT MAX(n.\"Version\") FROM factory_document_versions AS n " +
                "WHERE n.tenant_id = d.tenant_id AND n.\"Factory\" = d.\"Factory\" AND n.\"Name\" = d.\"Name\") - 3;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The removed versions are gone; going back keeps the three that are left.
        }
    }
}
