/*
  bank-reporting-platform
  Migration: 0001_create_app_state_snapshots.sql
  Purpose  : Create base snapshot table for SQL Server persistence.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.tables t
    INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE t.name = N'AppStateSnapshots'
      AND s.name = N'dbo'
)
BEGIN
    CREATE TABLE [dbo].[AppStateSnapshots]
    (
        [Id]        INT                NOT NULL,
        [Payload]   NVARCHAR(MAX)      NOT NULL,
        [UpdatedAt] DATETIMEOFFSET(7)  NOT NULL,
        CONSTRAINT [PK_AppStateSnapshots] PRIMARY KEY CLUSTERED ([Id] ASC)
    );
END;
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.default_constraints dc
    INNER JOIN sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
    INNER JOIN sys.tables t ON t.object_id = c.object_id
    INNER JOIN sys.schemas s ON s.schema_id = t.schema_id
    WHERE dc.name = N'DF_AppStateSnapshots_UpdatedAt'
      AND t.name = N'AppStateSnapshots'
      AND s.name = N'dbo'
)
BEGIN
    ALTER TABLE [dbo].[AppStateSnapshots]
    ADD CONSTRAINT [DF_AppStateSnapshots_UpdatedAt]
    DEFAULT (SYSUTCDATETIME()) FOR [UpdatedAt];
END;
GO
