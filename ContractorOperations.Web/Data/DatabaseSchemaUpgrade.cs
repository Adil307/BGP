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


-- v3.0 document, service approval, signature/stamp, released item number and job lifecycle upgrade.
IF OBJECT_ID(N'[SystemSettings]', N'U') IS NOT NULL AND COL_LENGTH('SystemSettings','ServiceApprovalPrefix') IS NULL
    ALTER TABLE [SystemSettings] ADD [ServiceApprovalPrefix] nvarchar(20) NOT NULL CONSTRAINT [DF_SystemSettings_ServiceApprovalPrefix] DEFAULT('SA');

IF OBJECT_ID(N'[Jobs]', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH('Jobs','IsAccepted') IS NULL ALTER TABLE [Jobs] ADD [IsAccepted] bit NOT NULL CONSTRAINT [DF_Jobs_IsAccepted] DEFAULT(0);
    IF COL_LENGTH('Jobs','AcceptedAt') IS NULL ALTER TABLE [Jobs] ADD [AcceptedAt] datetime2 NULL;
    IF COL_LENGTH('Jobs','AcceptedByUserId') IS NULL ALTER TABLE [Jobs] ADD [AcceptedByUserId] nvarchar(450) NULL;
    IF COL_LENGTH('Jobs','CancelledAt') IS NULL ALTER TABLE [Jobs] ADD [CancelledAt] datetime2 NULL;
    IF COL_LENGTH('Jobs','CancelledByUserId') IS NULL ALTER TABLE [Jobs] ADD [CancelledByUserId] nvarchar(450) NULL;
    IF COL_LENGTH('Jobs','CancellationReason') IS NULL ALTER TABLE [Jobs] ADD [CancellationReason] nvarchar(1000) NULL;
    IF COL_LENGTH('Jobs','CancellationNotes') IS NULL ALTER TABLE [Jobs] ADD [CancellationNotes] nvarchar(1500) NULL;
    IF COL_LENGTH('Jobs','PreviousStatus') IS NULL ALTER TABLE [Jobs] ADD [PreviousStatus] int NULL;
END;

IF OBJECT_ID(N'[JobLines]', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH('JobLines','ItemSerialNumber') IS NULL
        ALTER TABLE [JobLines] ADD [ItemSerialNumber] nvarchar(20) NULL;

    IF COL_LENGTH('JobLines','ItemSerialNumber') IS NOT NULL
       AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'[JobLines]') AND name = N'IX_JobLines_JobId_ItemSerialNumber')
    BEGIN
        EXEC(N'CREATE UNIQUE INDEX [IX_JobLines_JobId_ItemSerialNumber] ON [JobLines]([JobId],[ItemSerialNumber]) WHERE [ItemSerialNumber] IS NOT NULL;');
    END;
END;

IF OBJECT_ID(N'[DocumentRecords]', N'U') IS NULL
BEGIN
    CREATE TABLE [DocumentRecords](
        [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_DocumentRecords] PRIMARY KEY,
        [OriginalFileName] nvarchar(260) NOT NULL,
        [StoredFileName] nvarchar(260) NOT NULL,
        [ContentType] nvarchar(160) NOT NULL,
        [SizeBytes] bigint NOT NULL,
        [StoragePath] nvarchar(500) NOT NULL,
        [ReferenceType] nvarchar(60) NOT NULL,
        [ReferenceId] nvarchar(80) NOT NULL,
        [ReferenceNumber] nvarchar(160) NULL,
        [RequisitionNumber] nvarchar(80) NULL,
        [JobNumber] nvarchar(40) NULL,
        [ServiceApprovalNumber] nvarchar(40) NULL,
        [ProjectId] int NULL,
        [DepartmentId] int NULL,
        [Notes] nvarchar(1000) NULL,
        [UploadedByUserId] nvarchar(450) NOT NULL,
        [UploadedAt] datetime2 NOT NULL CONSTRAINT [DF_DocumentRecords_UploadedAt] DEFAULT(SYSUTCDATETIME()),
        [IsDeleted] bit NOT NULL CONSTRAINT [DF_DocumentRecords_IsDeleted] DEFAULT(0),
        [DeletedByUserId] nvarchar(450) NULL,
        [DeletedAt] datetime2 NULL
    );
    CREATE INDEX [IX_DocumentRecords_ReferenceType_ReferenceId_IsDeleted] ON [DocumentRecords]([ReferenceType],[ReferenceId],[IsDeleted]);
    CREATE INDEX [IX_DocumentRecords_RequisitionNumber] ON [DocumentRecords]([RequisitionNumber]);
    CREATE INDEX [IX_DocumentRecords_JobNumber] ON [DocumentRecords]([JobNumber]);
    CREATE INDEX [IX_DocumentRecords_ServiceApprovalNumber] ON [DocumentRecords]([ServiceApprovalNumber]);
END;

IF OBJECT_ID(N'[DocumentTemplates]', N'U') IS NULL
BEGIN
    CREATE TABLE [DocumentTemplates](
        [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_DocumentTemplates] PRIMARY KEY,
        [Name] nvarchar(160) NOT NULL,
        [DocumentType] nvarchar(60) NOT NULL,
        [HtmlContent] nvarchar(max) NOT NULL,
        [IsActive] bit NOT NULL CONSTRAINT [DF_DocumentTemplates_IsActive] DEFAULT(1),
        [CreatedByUserId] nvarchar(450) NOT NULL,
        [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_DocumentTemplates_CreatedAt] DEFAULT(SYSUTCDATETIME()),
        [UpdatedAt] datetime2 NOT NULL CONSTRAINT [DF_DocumentTemplates_UpdatedAt] DEFAULT(SYSUTCDATETIME())
    );
    CREATE INDEX [IX_DocumentTemplates_Name] ON [DocumentTemplates]([Name]);
END;

IF OBJECT_ID(N'[OfficialDocumentAssets]', N'U') IS NULL
BEGIN
    CREATE TABLE [OfficialDocumentAssets](
        [Id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_OfficialDocumentAssets] PRIMARY KEY,
        [AssetType] nvarchar(40) NOT NULL,
        [OriginalFileName] nvarchar(260) NOT NULL,
        [StoredFileName] nvarchar(260) NOT NULL,
        [ContentType] nvarchar(160) NOT NULL,
        [SizeBytes] bigint NOT NULL,
        [StoragePath] nvarchar(500) NOT NULL,
        [IsActive] bit NOT NULL CONSTRAINT [DF_OfficialDocumentAssets_IsActive] DEFAULT(1),
        [UpdatedByUserId] nvarchar(450) NOT NULL,
        [UpdatedAt] datetime2 NOT NULL CONSTRAINT [DF_OfficialDocumentAssets_UpdatedAt] DEFAULT(SYSUTCDATETIME())
    );
    CREATE UNIQUE INDEX [IX_OfficialDocumentAssets_AssetType_Active] ON [OfficialDocumentAssets]([AssetType]) WHERE [IsActive] = 1;
END;

IF OBJECT_ID(N'[DocumentAssetUsages]', N'U') IS NULL
BEGIN
    CREATE TABLE [DocumentAssetUsages](
        [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_DocumentAssetUsages] PRIMARY KEY,
        [OfficialDocumentAssetId] int NOT NULL,
        [ReferenceType] nvarchar(60) NOT NULL,
        [ReferenceId] nvarchar(80) NOT NULL,
        [ReferenceNumber] nvarchar(160) NULL,
        [AppliedByUserId] nvarchar(450) NOT NULL,
        [AppliedAt] datetime2 NOT NULL CONSTRAINT [DF_DocumentAssetUsages_AppliedAt] DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT [FK_DocumentAssetUsages_OfficialDocumentAssets] FOREIGN KEY([OfficialDocumentAssetId]) REFERENCES [OfficialDocumentAssets]([Id])
    );
    CREATE INDEX [IX_DocumentAssetUsages_Reference] ON [DocumentAssetUsages]([ReferenceType],[ReferenceId]);
END;

IF OBJECT_ID(N'[ServiceApprovals]', N'U') IS NULL
BEGIN
    CREATE TABLE [ServiceApprovals](
        [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_ServiceApprovals] PRIMARY KEY,
        [ApprovalNumber] nvarchar(40) NULL,
        [RequisitionNumber] nvarchar(80) NOT NULL,
        [ProjectId] int NOT NULL,
        [DepartmentId] int NULL,
        [ApprovalDate] datetime2 NOT NULL,
        [ServiceAmount] decimal(18,2) NOT NULL,
        [CurrencyId] int NULL,
        [Description] nvarchar(2000) NOT NULL,
        [ServiceType] nvarchar(80) NOT NULL CONSTRAINT [DF_ServiceApprovals_ServiceType] DEFAULT('Expenditure'),
        [VendorSelectionMethod] nvarchar(120) NULL,
        [Status] int NOT NULL CONSTRAINT [DF_ServiceApprovals_Status] DEFAULT(0),
        [CurrentStage] int NOT NULL CONSTRAINT [DF_ServiceApprovals_CurrentStage] DEFAULT(0),
        [RejectionReason] nvarchar(1500) NULL,
        [CreatedByUserId] nvarchar(450) NOT NULL,
        [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_ServiceApprovals_CreatedAt] DEFAULT(SYSUTCDATETIME()),
        [UpdatedAt] datetime2 NOT NULL CONSTRAINT [DF_ServiceApprovals_UpdatedAt] DEFAULT(SYSUTCDATETIME()),
        [FinalApprovedAt] datetime2 NULL,
        CONSTRAINT [FK_ServiceApprovals_Projects] FOREIGN KEY([ProjectId]) REFERENCES [Projects]([Id]),
        CONSTRAINT [FK_ServiceApprovals_Departments] FOREIGN KEY([DepartmentId]) REFERENCES [Departments]([Id]),
        CONSTRAINT [FK_ServiceApprovals_Currencies] FOREIGN KEY([CurrencyId]) REFERENCES [Currencies]([Id])
    );
    CREATE UNIQUE INDEX [IX_ServiceApprovals_ApprovalNumber] ON [ServiceApprovals]([ApprovalNumber]) WHERE [ApprovalNumber] IS NOT NULL;
    CREATE INDEX [IX_ServiceApprovals_ProjectId_RequisitionNumber] ON [ServiceApprovals]([ProjectId],[RequisitionNumber]);
END;

IF OBJECT_ID(N'[ServiceApprovalStages]', N'U') IS NULL
BEGIN
    CREATE TABLE [ServiceApprovalStages](
        [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_ServiceApprovalStages] PRIMARY KEY,
        [ServiceApprovalId] bigint NOT NULL,
        [StageNumber] int NOT NULL,
        [Title] nvarchar(160) NOT NULL,
        [ApproverUserId] nvarchar(450) NULL,
        [Status] int NOT NULL CONSTRAINT [DF_ServiceApprovalStages_Status] DEFAULT(0),
        [Comment] nvarchar(1500) NULL,
        [DecidedByUserId] nvarchar(450) NULL,
        [DecidedAt] datetime2 NULL,
        CONSTRAINT [FK_ServiceApprovalStages_ServiceApprovals] FOREIGN KEY([ServiceApprovalId]) REFERENCES [ServiceApprovals]([Id]) ON DELETE CASCADE
    );
    CREATE UNIQUE INDEX [IX_ServiceApprovalStages_ServiceApprovalId_StageNumber] ON [ServiceApprovalStages]([ServiceApprovalId],[StageNumber]);
END;

IF OBJECT_ID(N'[ReleasedItemNumbers]', N'U') IS NULL
BEGIN
    CREATE TABLE [ReleasedItemNumbers](
        [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_ReleasedItemNumbers] PRIMARY KEY,
        [ProjectSheetId] int NOT NULL,
        [RequisitionNumber] nvarchar(80) NOT NULL,
        [ItemNumber] int NOT NULL,
        [ReleasedFromRowId] bigint NULL,
        [ReleasedByUserId] nvarchar(450) NOT NULL,
        [ReleasedAt] datetime2 NOT NULL CONSTRAINT [DF_ReleasedItemNumbers_ReleasedAt] DEFAULT(SYSUTCDATETIME()),
        [IsUsed] bit NOT NULL CONSTRAINT [DF_ReleasedItemNumbers_IsUsed] DEFAULT(0),
        [UsedByRowId] bigint NULL,
        [UsedAt] datetime2 NULL
    );
    CREATE UNIQUE INDEX [IX_ReleasedItemNumbers_ProjectSheetId_RequisitionNumber_ItemNumber] ON [ReleasedItemNumbers]([ProjectSheetId],[RequisitionNumber],[ItemNumber]);
END;

-- v3.1 company document library and individual signature architecture.
IF OBJECT_ID(N'[DocumentTemplates]', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH('DocumentTemplates','Description') IS NULL EXEC(N'ALTER TABLE [DocumentTemplates] ADD [Description] nvarchar(1000) NULL;');
    IF COL_LENGTH('DocumentTemplates','EditableFieldLabels') IS NULL EXEC(N'ALTER TABLE [DocumentTemplates] ADD [EditableFieldLabels] nvarchar(1000) NULL;');
    IF COL_LENGTH('DocumentTemplates','SignatureSlotLabels') IS NULL EXEC(N'ALTER TABLE [DocumentTemplates] ADD [SignatureSlotLabels] nvarchar(1000) NULL;');
    IF COL_LENGTH('DocumentTemplates','SourceOriginalFileName') IS NULL EXEC(N'ALTER TABLE [DocumentTemplates] ADD [SourceOriginalFileName] nvarchar(260) NULL;');
    IF COL_LENGTH('DocumentTemplates','SourceStoredFileName') IS NULL EXEC(N'ALTER TABLE [DocumentTemplates] ADD [SourceStoredFileName] nvarchar(260) NULL;');
    IF COL_LENGTH('DocumentTemplates','SourceContentType') IS NULL EXEC(N'ALTER TABLE [DocumentTemplates] ADD [SourceContentType] nvarchar(160) NULL;');
    IF COL_LENGTH('DocumentTemplates','SourceStoragePath') IS NULL EXEC(N'ALTER TABLE [DocumentTemplates] ADD [SourceStoragePath] nvarchar(500) NULL;');
    IF COL_LENGTH('DocumentTemplates','SourceSizeBytes') IS NULL EXEC(N'ALTER TABLE [DocumentTemplates] ADD [SourceSizeBytes] bigint NULL;');
    IF COL_LENGTH('DocumentTemplates','SourceUploadedAt') IS NULL EXEC(N'ALTER TABLE [DocumentTemplates] ADD [SourceUploadedAt] datetime2 NULL;');
END;

IF OBJECT_ID(N'[UserSignatures]', N'U') IS NULL
BEGIN
    CREATE TABLE [UserSignatures](
        [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_UserSignatures] PRIMARY KEY,
        [UserId] nvarchar(450) NOT NULL,
        [OriginalFileName] nvarchar(260) NOT NULL,
        [StoredFileName] nvarchar(260) NOT NULL,
        [ContentType] nvarchar(160) NOT NULL,
        [SizeBytes] bigint NOT NULL,
        [StoragePath] nvarchar(500) NOT NULL,
        [IsActive] bit NOT NULL CONSTRAINT [DF_UserSignatures_IsActive] DEFAULT(1),
        [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_UserSignatures_CreatedAt] DEFAULT(SYSUTCDATETIME()),
        [UpdatedAt] datetime2 NOT NULL CONSTRAINT [DF_UserSignatures_UpdatedAt] DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT [FK_UserSignatures_AspNetUsers] FOREIGN KEY([UserId]) REFERENCES [AspNetUsers]([Id])
    );
    CREATE UNIQUE INDEX [IX_UserSignatures_UserId_Active] ON [UserSignatures]([UserId]) WHERE [IsActive] = 1;
END;

IF OBJECT_ID(N'[ServiceApprovalStages]', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH('ServiceApprovalStages','UserSignatureId') IS NULL EXEC(N'ALTER TABLE [ServiceApprovalStages] ADD [UserSignatureId] bigint NULL;');
    IF COL_LENGTH('ServiceApprovalStages','SignatureAppliedAt') IS NULL EXEC(N'ALTER TABLE [ServiceApprovalStages] ADD [SignatureAppliedAt] datetime2 NULL;');
    IF NOT EXISTS (
        SELECT 1 FROM sys.foreign_key_columns fkc
        WHERE fkc.parent_object_id = OBJECT_ID(N'[ServiceApprovalStages]')
          AND fkc.parent_column_id = COLUMNPROPERTY(OBJECT_ID(N'[ServiceApprovalStages]'), 'UserSignatureId', 'ColumnId')
    )
        EXEC(N'ALTER TABLE [ServiceApprovalStages] ADD CONSTRAINT [FK_ServiceApprovalStages_UserSignatures] FOREIGN KEY([UserSignatureId]) REFERENCES [UserSignatures]([Id]);');
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'[ServiceApprovalStages]') AND name = N'IX_ServiceApprovalStages_UserSignatureId')
        EXEC(N'CREATE INDEX [IX_ServiceApprovalStages_UserSignatureId] ON [ServiceApprovalStages]([UserSignatureId]);');
END;

IF OBJECT_ID(N'[DocumentAssetUsages]', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH('DocumentAssetUsages','IsRemoved') IS NULL EXEC(N'ALTER TABLE [DocumentAssetUsages] ADD [IsRemoved] bit NOT NULL CONSTRAINT [DF_DocumentAssetUsages_IsRemoved] DEFAULT(0);');
    IF COL_LENGTH('DocumentAssetUsages','RemovedByUserId') IS NULL EXEC(N'ALTER TABLE [DocumentAssetUsages] ADD [RemovedByUserId] nvarchar(450) NULL;');
    IF COL_LENGTH('DocumentAssetUsages','RemovedAt') IS NULL EXEC(N'ALTER TABLE [DocumentAssetUsages] ADD [RemovedAt] datetime2 NULL;');
END;

IF OBJECT_ID(N'[GeneratedCompanyDocuments]', N'U') IS NULL
BEGIN
    CREATE TABLE [GeneratedCompanyDocuments](
        [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_GeneratedCompanyDocuments] PRIMARY KEY,
        [DocumentTemplateId] int NOT NULL,
        [Title] nvarchar(180) NOT NULL,
        [ReferenceType] nvarchar(60) NOT NULL,
        [ReferenceId] nvarchar(80) NOT NULL,
        [ReferenceNumber] nvarchar(160) NULL,
        [RequisitionNumber] nvarchar(80) NULL,
        [ProjectId] int NULL,
        [DepartmentId] int NULL,
        [ServiceApprovalId] bigint NULL,
        [JobId] int NULL,
        [TemplateHtmlSnapshot] nvarchar(max) NOT NULL,
        [EditableFieldLabelsSnapshot] nvarchar(1000) NULL,
        [SignatureSlotLabelsSnapshot] nvarchar(1000) NULL,
        [CustomText1] nvarchar(2000) NULL,
        [CustomText2] nvarchar(2000) NULL,
        [CustomText3] nvarchar(2000) NULL,
        [CustomText4] nvarchar(2000) NULL,
        [Status] int NOT NULL CONSTRAINT [DF_GeneratedCompanyDocuments_Status] DEFAULT(0),
        [CreatedByUserId] nvarchar(450) NOT NULL,
        [CreatedAt] datetime2 NOT NULL CONSTRAINT [DF_GeneratedCompanyDocuments_CreatedAt] DEFAULT(SYSUTCDATETIME()),
        [UpdatedByUserId] nvarchar(450) NOT NULL,
        [UpdatedAt] datetime2 NOT NULL CONSTRAINT [DF_GeneratedCompanyDocuments_UpdatedAt] DEFAULT(SYSUTCDATETIME()),
        [FinalizedByUserId] nvarchar(450) NULL,
        [FinalizedAt] datetime2 NULL,
        [FinalHtmlSnapshot] nvarchar(max) NULL,
        [VoidedByUserId] nvarchar(450) NULL,
        [VoidedAt] datetime2 NULL,
        [VoidReason] nvarchar(1000) NULL,
        CONSTRAINT [FK_GeneratedCompanyDocuments_DocumentTemplates] FOREIGN KEY([DocumentTemplateId]) REFERENCES [DocumentTemplates]([Id]),
        CONSTRAINT [FK_GeneratedCompanyDocuments_Projects] FOREIGN KEY([ProjectId]) REFERENCES [Projects]([Id]),
        CONSTRAINT [FK_GeneratedCompanyDocuments_Departments] FOREIGN KEY([DepartmentId]) REFERENCES [Departments]([Id]),
        CONSTRAINT [FK_GeneratedCompanyDocuments_ServiceApprovals] FOREIGN KEY([ServiceApprovalId]) REFERENCES [ServiceApprovals]([Id]),
        CONSTRAINT [FK_GeneratedCompanyDocuments_Jobs] FOREIGN KEY([JobId]) REFERENCES [Jobs]([Id])
    );
    CREATE INDEX [IX_GeneratedCompanyDocuments_Reference] ON [GeneratedCompanyDocuments]([ReferenceType],[ReferenceId],[Status]);
    CREATE INDEX [IX_GeneratedCompanyDocuments_CreatedAt] ON [GeneratedCompanyDocuments]([CreatedAt]);
    CREATE INDEX [IX_GeneratedCompanyDocuments_ProjectId] ON [GeneratedCompanyDocuments]([ProjectId]);
    CREATE INDEX [IX_GeneratedCompanyDocuments_DepartmentId] ON [GeneratedCompanyDocuments]([DepartmentId]);
    CREATE INDEX [IX_GeneratedCompanyDocuments_ServiceApprovalId] ON [GeneratedCompanyDocuments]([ServiceApprovalId]);
    CREATE INDEX [IX_GeneratedCompanyDocuments_JobId] ON [GeneratedCompanyDocuments]([JobId]);
END;

IF OBJECT_ID(N'[GeneratedDocumentSignatures]', N'U') IS NULL
BEGIN
    CREATE TABLE [GeneratedDocumentSignatures](
        [Id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_GeneratedDocumentSignatures] PRIMARY KEY,
        [GeneratedCompanyDocumentId] bigint NOT NULL,
        [SlotNumber] int NOT NULL,
        [UserSignatureId] bigint NOT NULL,
        [AppliedByUserId] nvarchar(450) NOT NULL,
        [AppliedAt] datetime2 NOT NULL CONSTRAINT [DF_GeneratedDocumentSignatures_AppliedAt] DEFAULT(SYSUTCDATETIME()),
        CONSTRAINT [FK_GeneratedDocumentSignatures_Documents] FOREIGN KEY([GeneratedCompanyDocumentId]) REFERENCES [GeneratedCompanyDocuments]([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_GeneratedDocumentSignatures_UserSignatures] FOREIGN KEY([UserSignatureId]) REFERENCES [UserSignatures]([Id])
    );
    CREATE UNIQUE INDEX [IX_GeneratedDocumentSignatures_Document_Slot] ON [GeneratedDocumentSignatures]([GeneratedCompanyDocumentId],[SlotNumber]);
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
