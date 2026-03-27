/*
  bank-reporting-platform
  Migration: 0003_create_schema_migrations.sql
  Purpose  : Track applied SQL migrations (id + checksum + applied time).
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.tables t
    INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE t.name = N'SchemaMigrations'
      AND s.name = N'dbo'
)
BEGIN
    CREATE TABLE [dbo].[SchemaMigrations]
    (
        [MigrationId]    NVARCHAR(64)      NOT NULL,
        [ScriptName]     NVARCHAR(260)     NOT NULL,
        [ScriptChecksum] NVARCHAR(64)      NOT NULL,
        [AppliedAt]      DATETIMEOFFSET(7) NOT NULL,
        CONSTRAINT [PK_SchemaMigrations] PRIMARY KEY CLUSTERED ([MigrationId] ASC)
    );
END;
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.default_constraints dc
    INNER JOIN sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
    INNER JOIN sys.tables t ON t.object_id = c.object_id
    INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE dc.name = N'DF_SchemaMigrations_AppliedAt'
      AND t.name = N'SchemaMigrations'
      AND s.name = N'dbo'
)
BEGIN
    ALTER TABLE [dbo].[SchemaMigrations]
    ADD CONSTRAINT [DF_SchemaMigrations_AppliedAt]
    DEFAULT (SYSUTCDATETIME()) FOR [AppliedAt];
END;
GO
