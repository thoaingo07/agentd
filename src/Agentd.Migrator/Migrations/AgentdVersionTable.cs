using FluentMigrator.Runner.VersionTableInfo;

namespace Agentd.Migrator.Migrations;

/// <summary>FluentMigrator's version table, kept in the <c>agentd</c> schema.</summary>
public sealed class AgentdVersionTable : IVersionTableMetaData
{
    public bool OwnsSchema => true;

    public string SchemaName => "agentd";

    public string TableName => "schema_version";

    public string ColumnName => "version";

    public string DescriptionColumnName => "description";

    public string UniqueIndexName => "ux_schema_version";

    public string AppliedOnColumnName => "applied_on";

    public bool CreateWithPrimaryKey => false;   // the unique index on version is enough (FluentMigrator default)
}
