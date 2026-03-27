/*
  bank-reporting-platform
  Migration: 0002_seed_initial_snapshot_row.sql
  Purpose  : Ensure the singleton snapshot row (Id=1) exists for MERGE updates.
*/

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO

IF NOT EXISTS (
    SELECT 1
    FROM [dbo].[AppStateSnapshots]
    WHERE [Id] = 1
)
BEGIN
    INSERT INTO [dbo].[AppStateSnapshots] ([Id], [Payload], [UpdatedAt])
    VALUES (1, N'{}', SYSUTCDATETIME());
END;
GO
