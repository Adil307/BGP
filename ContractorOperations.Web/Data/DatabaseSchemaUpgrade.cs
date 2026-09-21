using Microsoft.EntityFrameworkCore;

namespace ContractorOperations.Web.Data;

public static class DatabaseSchemaUpgrade
{
    public static async Task ApplyAsync(ApplicationDbContext db)
    {
        var sql = @"
IF OBJECT_ID(N'[Countries]', N'U') IS NULL
BEGIN
    CREATE TABLE [Countries](
        [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_Countries] PRIMARY KEY,
        [Code] nvarchar(10) NOT NULL,
        [Name] nvarchar(120) NOT NULL,
        [IsActive] bit NOT NULL CONSTRAINT [DF_Countries_IsActive] DEFAULT(1),
        [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_Countries_CreatedAt] DEFAULT(SYSUTCDATETIME())
    );
    CREATE UNIQUE INDEX [IX_Countries_Code] ON [Countries]([Code]);
END;

IF OBJECT_ID(N'[BusinessSequences]', N'U') IS NULL
BEGIN
    CREATE TABLE [BusinessSequences](
        [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_BusinessSequences] PRIMARY KEY,
        [Prefix] nvarchar(20) NOT NULL,
        [Year] int NOT NULL,
        [LastNumber] int NOT NULL
    );
    CREATE UNIQUE INDEX [IX_BusinessSequences_Prefix_Year] ON [BusinessSequences]([Prefix],[Year]);
END;

IF OBJECT_ID(N'[ProjectWorkspaces]', N'U') IS NULL
BEGIN
    CREATE TABLE [ProjectWorkspaces](
        [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_ProjectWorkspaces] PRIMARY KEY,
        [ProjectId] int NOT NULL,
        [CountryId] int NOT NULL,
        [UniqueId] nvarchar(30) NOT NULL,
        [Description] nvarchar(1500) NULL,
        [ProjectManager] nvarchar(160) NULL,
        [StartDate] datetime2 NULL,
        [Deadline] datetime2 NULL,
        [Budget] decimal(18,2) NULL,
        [Priority] nvarchar(20) NOT NULL CONSTRAINT [DF_ProjectWorkspaces_Priority] DEFAULT('Medium'),
        [Status] nvarchar(30) NOT NULL CONSTRAINT [DF_ProjectWorkspaces_Status] DEFAULT('Active'),
        [ProgressPercent] int NOT NULL CONSTRAINT [DF_ProjectWorkspaces_Progress] DEFAULT(0),
        [Notes] nvarchar(2000) NULL,
        [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_ProjectWorkspaces_CreatedAt] DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT [FK_ProjectWorkspaces_Projects_ProjectId] FOREIGN KEY([ProjectId]) REFERENCES [Projects]([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_ProjectWorkspaces_Countries_CountryId] FOREIGN KEY([CountryId]) REFERENCES [Countries]([Id])
    );
    CREATE UNIQUE INDEX [IX_ProjectWorkspaces_ProjectId] ON [ProjectWorkspaces]([ProjectId]);
    CREATE UNIQUE INDEX [IX_ProjectWorkspaces_UniqueId] ON [ProjectWorkspaces]([UniqueId]);
END;

IF OBJECT_ID(N'[ProjectSheets]', N'U') IS NULL
BEGIN
    CREATE TABLE [ProjectSheets](
        [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_ProjectSheets] PRIMARY KEY,
        [UniqueId] nvarchar(30) NOT NULL,
        [Name] nvarchar(160) NOT NULL,
        [ProjectId] int NULL,
        [DepartmentId] int NULL,
        [SortOrder] int NOT NULL CONSTRAINT [DF_ProjectSheets_SortOrder] DEFAULT(0),
        [IsActive] bit NOT NULL CONSTRAINT [DF_ProjectSheets_IsActive] DEFAULT(1),
        [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_ProjectSheets_CreatedAt] DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT [FK_ProjectSheets_Projects_ProjectId] FOREIGN KEY([ProjectId]) REFERENCES [Projects]([Id]),
        CONSTRAINT [FK_ProjectSheets_Departments_DepartmentId] FOREIGN KEY([DepartmentId]) REFERENCES [Departments]([Id])
    );
    CREATE UNIQUE INDEX [IX_ProjectSheets_UniqueId] ON [ProjectSheets]([UniqueId]);
    CREATE INDEX [IX_ProjectSheets_ProjectId] ON [ProjectSheets]([ProjectId]);
    CREATE INDEX [IX_ProjectSheets_DepartmentId] ON [ProjectSheets]([DepartmentId]);
END;

IF OBJECT_ID(N'[SheetColumns]', N'U') IS NULL
BEGIN
    CREATE TABLE [SheetColumns](
        [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_SheetColumns] PRIMARY KEY,
        [ProjectSheetId] int NOT NULL,
        [Name] nvarchar(120) NOT NULL,
        [ColumnType] int NOT NULL,
        [SortOrder] int NOT NULL CONSTRAINT [DF_SheetColumns_SortOrder] DEFAULT(0),
        [IsRequired] bit NOT NULL CONSTRAINT [DF_SheetColumns_IsRequired] DEFAULT(0),
        [Options] nvarchar(2000) NULL,
        CONSTRAINT [FK_SheetColumns_ProjectSheets_ProjectSheetId] FOREIGN KEY([ProjectSheetId]) REFERENCES [ProjectSheets]([Id]) ON DELETE CASCADE
    );
    CREATE UNIQUE INDEX [IX_SheetColumns_ProjectSheetId_Name] ON [SheetColumns]([ProjectSheetId],[Name]);
END;

IF OBJECT_ID(N'[SheetRows]', N'U') IS NULL
BEGIN
    CREATE TABLE [SheetRows](
        [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_SheetRows] PRIMARY KEY,
        [ProjectSheetId] int NOT NULL,
        [UniqueId] nvarchar(40) NOT NULL,
        [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_SheetRows_CreatedAt] DEFAULT(SYSUTCDATETIME()),
        [UpdatedAt] datetime2 NOT NULL CONSTRAINT [DF_SheetRows_UpdatedAt] DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT [FK_SheetRows_ProjectSheets_ProjectSheetId] FOREIGN KEY([ProjectSheetId]) REFERENCES [ProjectSheets]([Id]) ON DELETE CASCADE
    );
    CREATE UNIQUE INDEX [IX_SheetRows_UniqueId] ON [SheetRows]([UniqueId]);
    CREATE INDEX [IX_SheetRows_ProjectSheetId] ON [SheetRows]([ProjectSheetId]);
END;

IF OBJECT_ID(N'[SheetCells]', N'U') IS NULL
BEGIN
    CREATE TABLE [SheetCells](
        [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_SheetCells] PRIMARY KEY,
        [SheetRowId] bigint NOT NULL,
        [SheetColumnId] int NOT NULL,
        [Value] nvarchar(max) NULL,
        CONSTRAINT [FK_SheetCells_SheetRows_SheetRowId] FOREIGN KEY([SheetRowId]) REFERENCES [SheetRows]([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_SheetCells_SheetColumns_SheetColumnId] FOREIGN KEY([SheetColumnId]) REFERENCES [SheetColumns]([Id])
    );
    CREATE UNIQUE INDEX [IX_SheetCells_SheetRowId_SheetColumnId] ON [SheetCells]([SheetRowId],[SheetColumnId]);
    CREATE INDEX [IX_SheetCells_SheetColumnId] ON [SheetCells]([SheetColumnId]);
END;

IF OBJECT_ID(N'[InventoryRequests]', N'U') IS NULL
BEGIN
    CREATE TABLE [InventoryRequests](
        [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_InventoryRequests] PRIMARY KEY,
        [RequestNumber] nvarchar(30) NOT NULL,
        [ItemName] nvarchar(160) NOT NULL,
        [Category] nvarchar(120) NULL,
        [Quantity] decimal(18,3) NOT NULL,
        [Unit] nvarchar(30) NOT NULL CONSTRAINT [DF_InventoryRequests_Unit] DEFAULT('EA'),
        [DepartmentId] int NOT NULL,
        [ProjectId] int NULL,
        [RequestedByUserId] nvarchar(450) NOT NULL,
        [RequestDate] datetime2 NOT NULL,
        [RequiredDate] datetime2 NULL,
        [Purpose] nvarchar(1200) NOT NULL,
        [Notes] nvarchar(1500) NULL,
        [Status] int NOT NULL CONSTRAINT [DF_InventoryRequests_Status] DEFAULT(0),
        [ApprovedByUserId] nvarchar(450) NULL,
        [ApprovalDate] datetime2 NULL,
        [ApprovedQuantity] decimal(18,3) NULL,
        [RejectionReason] nvarchar(1000) NULL,
        [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_InventoryRequests_CreatedAt] DEFAULT(SYSUTCDATETIME()),
        [UpdatedAt] datetime2 NOT NULL CONSTRAINT [DF_InventoryRequests_UpdatedAt] DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT [FK_InventoryRequests_Departments_DepartmentId] FOREIGN KEY([DepartmentId]) REFERENCES [Departments]([Id]),
        CONSTRAINT [FK_InventoryRequests_Projects_ProjectId] FOREIGN KEY([ProjectId]) REFERENCES [Projects]([Id]),
        CONSTRAINT [FK_InventoryRequests_AspNetUsers_RequestedByUserId] FOREIGN KEY([RequestedByUserId]) REFERENCES [AspNetUsers]([Id]),
        CONSTRAINT [FK_InventoryRequests_AspNetUsers_ApprovedByUserId] FOREIGN KEY([ApprovedByUserId]) REFERENCES [AspNetUsers]([Id])
    );
    CREATE UNIQUE INDEX [IX_InventoryRequests_RequestNumber] ON [InventoryRequests]([RequestNumber]);
    CREATE INDEX [IX_InventoryRequests_DepartmentId] ON [InventoryRequests]([DepartmentId]);
    CREATE INDEX [IX_InventoryRequests_ProjectId] ON [InventoryRequests]([ProjectId]);
    CREATE INDEX [IX_InventoryRequests_RequestedByUserId] ON [InventoryRequests]([RequestedByUserId]);
END;


IF OBJECT_ID(N'[WorkflowNotifications]', N'U') IS NULL
BEGIN
    CREATE TABLE [WorkflowNotifications](
        [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_WorkflowNotifications] PRIMARY KEY,
        [UserId] nvarchar(450) NOT NULL,
        [Title] nvarchar(160) NOT NULL,
        [Message] nvarchar(1000) NOT NULL,
        [Url] nvarchar(500) NULL,
        [IsRead] bit NOT NULL CONSTRAINT [DF_WorkflowNotifications_IsRead] DEFAULT(0),
        [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_WorkflowNotifications_CreatedAt] DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT [FK_WorkflowNotifications_AspNetUsers_UserId] FOREIGN KEY([UserId]) REFERENCES [AspNetUsers]([Id]) ON DELETE CASCADE
    );
    CREATE INDEX [IX_WorkflowNotifications_UserId_IsRead_CreatedAt]
        ON [WorkflowNotifications]([UserId],[IsRead],[CreatedAt]);
END;

-- Soft-deleted jobs must not block creation of a replacement job with the same
-- business details. Keep de-duplication unique only among active jobs.
IF OBJECT_ID(N'[Jobs]', N'U') IS NOT NULL
BEGIN
    IF EXISTS (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'[Jobs]')
          AND name = N'IX_Jobs_DeduplicationKey'
          AND (filter_definition IS NULL OR filter_definition NOT LIKE N'%IsDeleted%')
    )
    BEGIN
        DROP INDEX [IX_Jobs_DeduplicationKey] ON [Jobs];
    END;

    IF NOT EXISTS (
        SELECT 1
        FROM sys.indexes
        WHERE object_id = OBJECT_ID(N'[Jobs]')
          AND name = N'IX_Jobs_DeduplicationKey'
    )
    BEGIN
        CREATE UNIQUE INDEX [IX_Jobs_DeduplicationKey]
            ON [Jobs]([DeduplicationKey])
            WHERE [IsDeleted] = 0;
    END;
END;

";

        await db.Database.ExecuteSqlRawAsync(sql);
    }
}
