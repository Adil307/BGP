using ContractorOperations.Web.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace ContractorOperations.Web.Data;

public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options) { }

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Contractor> Contractors => Set<Contractor>();
    public DbSet<Currency> Currencies => Set<Currency>();
    public DbSet<Unit> Units => Set<Unit>();
    public DbSet<JobSequence> JobSequences => Set<JobSequence>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobLine> JobLines => Set<JobLine>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserPermission> UserPermissions => Set<UserPermission>();
    public DbSet<UserDepartment> UserDepartments => Set<UserDepartment>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<StockCategory> StockCategories => Set<StockCategory>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<StockItem> StockItems => Set<StockItem>();
    public DbSet<StockBalance> StockBalances => Set<StockBalance>();
    public DbSet<StockTransaction> StockTransactions => Set<StockTransaction>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<Country> Countries => Set<Country>();
    public DbSet<ProjectWorkspace> ProjectWorkspaces => Set<ProjectWorkspace>();
    public DbSet<BusinessSequence> BusinessSequences => Set<BusinessSequence>();
    public DbSet<ProjectSheet> ProjectSheets => Set<ProjectSheet>();
    public DbSet<SheetColumn> SheetColumns => Set<SheetColumn>();
    public DbSet<SheetRow> SheetRows => Set<SheetRow>();
    public DbSet<SheetCell> SheetCells => Set<SheetCell>();
    public DbSet<InventoryRequest> InventoryRequests => Set<InventoryRequest>();
    public DbSet<WorkflowNotification> WorkflowNotifications => Set<WorkflowNotification>();
    public DbSet<DocumentRecord> DocumentRecords => Set<DocumentRecord>();
    public DbSet<DocumentTemplate> DocumentTemplates => Set<DocumentTemplate>();
    public DbSet<UserSignature> UserSignatures => Set<UserSignature>();
    public DbSet<GeneratedCompanyDocument> GeneratedCompanyDocuments => Set<GeneratedCompanyDocument>();
    public DbSet<GeneratedDocumentSignature> GeneratedDocumentSignatures => Set<GeneratedDocumentSignature>();
    public DbSet<OfficialDocumentAsset> OfficialDocumentAssets => Set<OfficialDocumentAsset>();
    public DbSet<DocumentAssetUsage> DocumentAssetUsages => Set<DocumentAssetUsage>();
    public DbSet<ServiceApproval> ServiceApprovals => Set<ServiceApproval>();
    public DbSet<ServiceApprovalStage> ServiceApprovalStages => Set<ServiceApprovalStage>();
    public DbSet<ReleasedItemNumber> ReleasedItemNumbers => Set<ReleasedItemNumber>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Project>().HasIndex(x => x.Code).IsUnique();
        builder.Entity<Department>().HasIndex(x => x.Code).IsUnique();
        builder.Entity<Contractor>().HasIndex(x => x.Name).IsUnique();
        builder.Entity<Currency>().HasIndex(x => x.Code).IsUnique();
        builder.Entity<Unit>().HasIndex(x => x.Code).IsUnique();
        builder.Entity<StockCategory>().HasIndex(x => x.Name).IsUnique();
        builder.Entity<Warehouse>().HasIndex(x => x.Name).IsUnique();
        builder.Entity<JobSequence>().HasIndex(x => x.Year).IsUnique();
        builder.Entity<Job>().HasIndex(x => x.JobNumber).IsUnique();
        builder.Entity<Job>().HasIndex(x => x.DeduplicationKey).IsUnique().HasFilter("[IsDeleted] = 0");
        builder.Entity<JobLine>().HasIndex(x => new { x.JobId, x.Description, x.UnitId, x.CurrencyId, x.Quantity, x.UnitRate }).IsUnique();
        builder.Entity<JobLine>().HasIndex(x => new { x.JobId, x.ItemSerialNumber }).IsUnique().HasFilter("[ItemSerialNumber] IS NOT NULL");
        builder.Entity<Job>().HasQueryFilter(x => !x.IsDeleted);
        // JobLine is the required dependent of Job. Apply the same soft-delete
        // visibility rule so EF Core does not return orphaned line items when a
        // Job is hidden by the global query filter.
        builder.Entity<JobLine>().HasQueryFilter(x => !x.Job!.IsDeleted);
        builder.Entity<Permission>().HasIndex(x => x.Key).IsUnique();
        builder.Entity<StockItem>().HasIndex(x => x.ItemCode).IsUnique();
        builder.Entity<StockBalance>().HasIndex(x => new { x.StockItemId, x.WarehouseId }).IsUnique();
        builder.Entity<StockTransaction>()
            .HasIndex(x => new { x.StockItemId, x.WarehouseId, x.Type, x.ReferenceNo })
            .IsUnique()
            .HasFilter("[ReferenceNo] IS NOT NULL");

        builder.Entity<Country>().HasIndex(x => x.Code).IsUnique();
        builder.Entity<ProjectWorkspace>().HasIndex(x => x.ProjectId).IsUnique();
        builder.Entity<ProjectWorkspace>().HasIndex(x => x.UniqueId).IsUnique();
        builder.Entity<BusinessSequence>().HasIndex(x => new { x.Prefix, x.Year }).IsUnique();
        builder.Entity<ProjectSheet>().HasIndex(x => x.UniqueId).IsUnique();
        builder.Entity<SheetColumn>().HasIndex(x => new { x.ProjectSheetId, x.Name }).IsUnique();
        builder.Entity<SheetRow>().HasIndex(x => x.UniqueId).IsUnique();
        builder.Entity<SheetCell>().HasIndex(x => new { x.SheetRowId, x.SheetColumnId }).IsUnique();
        builder.Entity<InventoryRequest>().HasIndex(x => x.RequestNumber).IsUnique();
        builder.Entity<WorkflowNotification>().HasIndex(x => new { x.UserId, x.IsRead, x.CreatedAt });
        builder.Entity<DocumentRecord>().HasIndex(x => new { x.ReferenceType, x.ReferenceId, x.IsDeleted });
        builder.Entity<DocumentRecord>().HasIndex(x => x.RequisitionNumber);
        builder.Entity<DocumentRecord>().HasIndex(x => x.JobNumber);
        builder.Entity<DocumentRecord>().HasIndex(x => x.ServiceApprovalNumber);
        builder.Entity<DocumentTemplate>().HasIndex(x => x.Name);
        builder.Entity<UserSignature>().HasIndex(x => x.UserId).IsUnique().HasFilter("[IsActive] = 1");
        builder.Entity<GeneratedCompanyDocument>().HasIndex(x => new { x.ReferenceType, x.ReferenceId, x.Status });
        builder.Entity<GeneratedCompanyDocument>().HasIndex(x => x.CreatedAt);
        builder.Entity<GeneratedDocumentSignature>().HasIndex(x => new { x.GeneratedCompanyDocumentId, x.SlotNumber }).IsUnique();
        builder.Entity<OfficialDocumentAsset>().HasIndex(x => x.AssetType).IsUnique().HasFilter("[IsActive] = 1");
        builder.Entity<ServiceApproval>().HasIndex(x => x.ApprovalNumber).IsUnique().HasFilter("[ApprovalNumber] IS NOT NULL");
        builder.Entity<ServiceApproval>().HasIndex(x => new { x.ProjectId, x.RequisitionNumber });
        builder.Entity<ServiceApprovalStage>().HasIndex(x => new { x.ServiceApprovalId, x.StageNumber }).IsUnique();
        builder.Entity<ReleasedItemNumber>().HasIndex(x => new { x.ProjectSheetId, x.RequisitionNumber, x.ItemNumber }).IsUnique();

        builder.Entity<RolePermission>().HasKey(x => new { x.RoleId, x.PermissionId });
        builder.Entity<UserPermission>().HasKey(x => new { x.UserId, x.PermissionId });
        builder.Entity<UserDepartment>().HasKey(x => new { x.UserId, x.DepartmentId });

        builder.Entity<RolePermission>()
            .HasOne(x => x.Role).WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<UserPermission>()
            .HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<UserDepartment>()
            .HasOne(x => x.User).WithMany(x => x.UserDepartments).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

        builder.Entity<Job>()
            .HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<StockTransaction>()
            .HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);

        // Stock transactions are an accounting/inventory ledger and must never
        // be cascade-deleted through master records. SQL Server also rejects
        // the previous model because Currency -> StockItem -> StockTransaction
        // and Currency -> StockTransaction created multiple cascade paths.
        builder.Entity<StockTransaction>()
            .HasOne(x => x.StockItem).WithMany().HasForeignKey(x => x.StockItemId).OnDelete(DeleteBehavior.NoAction);
        builder.Entity<StockTransaction>()
            .HasOne(x => x.Warehouse).WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.NoAction);
        builder.Entity<StockTransaction>()
            .HasOne(x => x.Currency).WithMany().HasForeignKey(x => x.CurrencyId).OnDelete(DeleteBehavior.NoAction);
        builder.Entity<StockTransaction>()
            .HasOne(x => x.Contractor).WithMany().HasForeignKey(x => x.ContractorId).OnDelete(DeleteBehavior.NoAction);
        builder.Entity<StockTransaction>()
            .HasOne(x => x.Job).WithMany(x => x.StockTransactions).HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.NoAction);

        builder.Entity<ProjectWorkspace>()
            .HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<ProjectWorkspace>()
            .HasOne(x => x.Country).WithMany().HasForeignKey(x => x.CountryId).OnDelete(DeleteBehavior.NoAction);
        builder.Entity<ProjectSheet>()
            .HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.NoAction);
        builder.Entity<ProjectSheet>()
            .HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.NoAction);
        builder.Entity<SheetColumn>()
            .HasOne(x => x.ProjectSheet).WithMany(x => x.Columns).HasForeignKey(x => x.ProjectSheetId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<SheetRow>()
            .HasOne(x => x.ProjectSheet).WithMany(x => x.Rows).HasForeignKey(x => x.ProjectSheetId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<SheetCell>()
            .HasOne(x => x.SheetRow).WithMany(x => x.Cells).HasForeignKey(x => x.SheetRowId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<SheetCell>()
            .HasOne(x => x.SheetColumn).WithMany().HasForeignKey(x => x.SheetColumnId).OnDelete(DeleteBehavior.NoAction);
        builder.Entity<InventoryRequest>()
            .HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.NoAction);
        builder.Entity<InventoryRequest>()
            .HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.NoAction);
        builder.Entity<InventoryRequest>()
            .HasOne(x => x.RequestedByUser).WithMany().HasForeignKey(x => x.RequestedByUserId).OnDelete(DeleteBehavior.NoAction);
        builder.Entity<InventoryRequest>()
            .HasOne(x => x.ApprovedByUser).WithMany().HasForeignKey(x => x.ApprovedByUserId).OnDelete(DeleteBehavior.NoAction);
        builder.Entity<WorkflowNotification>()
            .HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<ServiceApproval>()
            .HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.NoAction);
        builder.Entity<ServiceApproval>()
            .HasOne(x => x.Department).WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.NoAction);
        builder.Entity<ServiceApproval>()
            .HasOne(x => x.Currency).WithMany().HasForeignKey(x => x.CurrencyId).OnDelete(DeleteBehavior.NoAction);
        builder.Entity<ServiceApprovalStage>()
            .HasOne(x => x.ServiceApproval).WithMany(x => x.Stages).HasForeignKey(x => x.ServiceApprovalId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<ServiceApprovalStage>()
            .HasOne(x => x.UserSignature).WithMany().HasForeignKey(x => x.UserSignatureId).OnDelete(DeleteBehavior.NoAction);
        builder.Entity<UserSignature>()
            .HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.NoAction);
        builder.Entity<GeneratedCompanyDocument>()
            .HasOne(x => x.DocumentTemplate).WithMany().HasForeignKey(x => x.DocumentTemplateId).OnDelete(DeleteBehavior.NoAction);
        builder.Entity<GeneratedCompanyDocument>()
            .HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.NoAction);
        builder.Entity<GeneratedCompanyDocument>()
            .HasOne<Department>().WithMany().HasForeignKey(x => x.DepartmentId).OnDelete(DeleteBehavior.NoAction);
        builder.Entity<GeneratedCompanyDocument>()
            .HasOne<ServiceApproval>().WithMany().HasForeignKey(x => x.ServiceApprovalId).OnDelete(DeleteBehavior.NoAction);
        builder.Entity<GeneratedCompanyDocument>()
            .HasOne<Job>().WithMany().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.NoAction);
        builder.Entity<GeneratedDocumentSignature>()
            .HasOne(x => x.GeneratedCompanyDocument).WithMany(x => x.Signatures).HasForeignKey(x => x.GeneratedCompanyDocumentId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<GeneratedDocumentSignature>()
            .HasOne(x => x.UserSignature).WithMany().HasForeignKey(x => x.UserSignatureId).OnDelete(DeleteBehavior.NoAction);
        builder.Entity<DocumentAssetUsage>()
            .HasOne(x => x.OfficialDocumentAsset).WithMany().HasForeignKey(x => x.OfficialDocumentAssetId).OnDelete(DeleteBehavior.NoAction);

        builder.Entity<JobLine>().Property(x => x.Quantity).HasPrecision(18, 3);
        builder.Entity<JobLine>().Property(x => x.UnitRate).HasPrecision(18, 2);
        builder.Entity<StockItem>().Property(x => x.ReorderLevel).HasPrecision(18, 3);
        builder.Entity<StockItem>().Property(x => x.UnitCost).HasPrecision(18, 2);
        builder.Entity<StockBalance>().Property(x => x.QuantityOnHand).HasPrecision(18, 3);
        builder.Entity<StockTransaction>().Property(x => x.Quantity).HasPrecision(18, 3);
        builder.Entity<StockTransaction>().Property(x => x.UnitCost).HasPrecision(18, 2);
        builder.Entity<ProjectWorkspace>().Property(x => x.Budget).HasPrecision(18, 2);
        builder.Entity<InventoryRequest>().Property(x => x.Quantity).HasPrecision(18, 3);
        builder.Entity<InventoryRequest>().Property(x => x.ApprovedQuantity).HasPrecision(18, 3);
        builder.Entity<ServiceApproval>().Property(x => x.ServiceAmount).HasPrecision(18, 2);
    }
}
