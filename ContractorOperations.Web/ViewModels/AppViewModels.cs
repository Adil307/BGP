using ContractorOperations.Web.Models;
using Microsoft.AspNetCore.Mvc.Rendering;
using System.ComponentModel.DataAnnotations;

namespace ContractorOperations.Web.ViewModels;

public class LoginVm
{
    [Required, EmailAddress] public string Email { get; set; } = string.Empty;
    [Required, DataType(DataType.Password)] public string Password { get; set; } = string.Empty;
    public bool RememberMe { get; set; }
    public string? ReturnUrl { get; set; }
}

public class DashboardVm
{
    public string UserName { get; set; } = string.Empty;
    public int TotalJobs { get; set; }
    public int CompletedJobs { get; set; }
    public int InProgressJobs { get; set; }
    public int OnHoldCancelledJobs { get; set; }
    public Dictionary<string,int> DepartmentJobs { get; set; } = new();
    public Dictionary<string,int> StatusJobs { get; set; } = new();
    public Dictionary<string,int> MonthlyJobs { get; set; } = new();
    public List<DepartmentAmountVm> DepartmentAmounts { get; set; } = new();
    public List<Job> RecentJobs { get; set; } = new();
    public int ProjectCount { get; set; }
    public int ActiveProjectCount { get; set; }
    public int CompletedProjectCount { get; set; }
    public int PendingProjectCount { get; set; }
    public int DepartmentCount { get; set; }
    public int ContractorCount { get; set; }
    public int CurrencyCount { get; set; }
    public int TotalStockItems { get; set; }
    public int HealthyStockItems { get; set; }
    public int LowStockItems { get; set; }
    public int OutOfStockItems { get; set; }
    public int WarehouseCount { get; set; }
    public int TotalInventoryRequests { get; set; }
    public int PendingInventoryRequests { get; set; }
    public int AcceptedInventoryRequests { get; set; }
    public int RejectedInventoryRequests { get; set; }
    public int SheetCount { get; set; }
    public Dictionary<string,decimal> InventoryValueByCurrency { get; set; } = new();
    public List<RoleSummaryVm> RoleSummaries { get; set; } = new();
}

public class RoleSummaryVm
{
    public string RoleName { get; set; } = string.Empty;
    public int Users { get; set; }
    public int PermissionCount { get; set; }
    public string AccessSummary { get; set; } = string.Empty;
}

public class DepartmentAmountVm
{
    public string Department { get; set; } = string.Empty;
    public int JobCount { get; set; }
    public decimal Quantity { get; set; }
    public Dictionary<string,decimal> Amounts { get; set; } = new();
}

public class JobLineInputVm
{
    public int? Id { get; set; }
    [Required, MaxLength(300)] public string Description { get; set; } = string.Empty;
    [Range(typeof(decimal), "0.001", "999999999")] public decimal Quantity { get; set; } = 1;
    [Range(typeof(decimal), "0", "999999999")] public decimal UnitRate { get; set; }
    [Range(1, int.MaxValue)] public int UnitId { get; set; }
    [Range(1, int.MaxValue)] public int CurrencyId { get; set; }
}

public class JobFormVm
{
    public int? Id { get; set; }
    public string JobNumber { get; set; } = "Auto-generated";
    [Range(1, int.MaxValue)] public int ProjectId { get; set; }
    [Range(1, int.MaxValue)] public int DepartmentId { get; set; }
    [Range(1, int.MaxValue)] public int ContractorId { get; set; }
    [Required, MaxLength(1000)] public string Description { get; set; } = string.Empty;
    [DataType(DataType.Date)] public DateTime JobDate { get; set; } = DateTime.Today;
    [DataType(DataType.Date)] public DateTime? ServiceStart { get; set; }
    [DataType(DataType.Date)] public DateTime? ServiceEnd { get; set; }
    public JobStatus Status { get; set; } = JobStatus.InProgress;
    [MaxLength(100)] public string? InvoiceNumber { get; set; }
    [DataType(DataType.Date)] public DateTime? InvoiceDate { get; set; }
    public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.Uninvoiced;
    [MaxLength(120)] public string? ProcessorName { get; set; }
    public List<JobLineInputVm> Lines { get; set; } = new() { new JobLineInputVm() };
    public IEnumerable<SelectListItem> Projects { get; set; } = Array.Empty<SelectListItem>();
    public IEnumerable<SelectListItem> Departments { get; set; } = Array.Empty<SelectListItem>();
    public IEnumerable<SelectListItem> Contractors { get; set; } = Array.Empty<SelectListItem>();
    public IEnumerable<SelectListItem> Units { get; set; } = Array.Empty<SelectListItem>();
    public IEnumerable<SelectListItem> Currencies { get; set; } = Array.Empty<SelectListItem>();
}

public class InventoryDashboardVm
{
    public List<StockItemRowVm> Items { get; set; } = new();
    public int TotalItems { get; set; }
    public int LowStock { get; set; }
    public int OutOfStock { get; set; }
    public int Warehouses { get; set; }
}

public class StockItemRowVm
{
    public int Id { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Unit { get; set; } = string.Empty;
    public decimal QuantityOnHand { get; set; }
    public decimal ReorderLevel { get; set; }
    public decimal UnitCost { get; set; }
    public string Currency { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}

public class StockItemFormVm
{
    public int? Id { get; set; }
    [Required, MaxLength(40)] public string ItemCode { get; set; } = string.Empty;
    [Required, MaxLength(160)] public string Name { get; set; } = string.Empty;
    [Range(1, int.MaxValue)] public int StockCategoryId { get; set; }
    [Range(1, int.MaxValue)] public int UnitId { get; set; }
    [Range(typeof(decimal), "0", "999999999")] public decimal ReorderLevel { get; set; }
    [Range(typeof(decimal), "0", "999999999")] public decimal UnitCost { get; set; }
    [Range(1, int.MaxValue)] public int CurrencyId { get; set; }
    public bool IsActive { get; set; } = true;
    public IEnumerable<SelectListItem> Categories { get; set; } = Array.Empty<SelectListItem>();
    public IEnumerable<SelectListItem> Units { get; set; } = Array.Empty<SelectListItem>();
    public IEnumerable<SelectListItem> Currencies { get; set; } = Array.Empty<SelectListItem>();
}

public class StockTransactionFormVm
{
    [Range(1, int.MaxValue)] public int StockItemId { get; set; }
    [Range(1, int.MaxValue)] public int WarehouseId { get; set; }
    public StockTransactionType Type { get; set; }
    [Range(typeof(decimal), "0.001", "999999999")] public decimal Quantity { get; set; }
    [Range(typeof(decimal), "0", "999999999")] public decimal UnitCost { get; set; }
    [Range(1, int.MaxValue)] public int CurrencyId { get; set; }
    public int? JobId { get; set; }
    public int? ContractorId { get; set; }
    [MaxLength(100)] public string? ReferenceNo { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
    public IEnumerable<SelectListItem> Items { get; set; } = Array.Empty<SelectListItem>();
    public IEnumerable<SelectListItem> Warehouses { get; set; } = Array.Empty<SelectListItem>();
    public IEnumerable<SelectListItem> Currencies { get; set; } = Array.Empty<SelectListItem>();
    public IEnumerable<SelectListItem> Jobs { get; set; } = Array.Empty<SelectListItem>();
    public IEnumerable<SelectListItem> Contractors { get; set; } = Array.Empty<SelectListItem>();
}

public class UserFormVm
{
    public string? Id { get; set; }
    [Required, MaxLength(120)] public string FullName { get; set; } = string.Empty;
    [Required, EmailAddress] public string Email { get; set; } = string.Empty;
    [DataType(DataType.Password)] public string? Password { get; set; }
    [Required] public string Role { get; set; } = "Department User";
    public List<int> DepartmentIds { get; set; } = new();
    public bool IsActive { get; set; } = true;
    public IEnumerable<SelectListItem> Roles { get; set; } = Array.Empty<SelectListItem>();
    public IEnumerable<SelectListItem> Departments { get; set; } = Array.Empty<SelectListItem>();
}

public class UserListVm
{
    public ApplicationUser User { get; set; } = new();
    public string Roles { get; set; } = string.Empty;
    public string Departments { get; set; } = string.Empty;
}

public class RolePermissionVm
{
    public string RoleId { get; set; } = string.Empty;
    public string RoleName { get; set; } = string.Empty;
    public List<Permission> Permissions { get; set; } = new();
    public HashSet<int> GrantedPermissionIds { get; set; } = new();
}

public class UserPermissionVm
{
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public List<Permission> Permissions { get; set; } = new();
    public Dictionary<int,string> Decisions { get; set; } = new();
}

public class MasterDataVm
{
    public string Type { get; set; } = "projects";
    public List<Project> Projects { get; set; } = new();
    public List<Department> Departments { get; set; } = new();
    public List<Contractor> Contractors { get; set; } = new();
    public List<Currency> Currencies { get; set; } = new();
    public List<Unit> Units { get; set; } = new();
    public List<StockCategory> StockCategories { get; set; } = new();
    public List<Warehouse> Warehouses { get; set; } = new();
}

public class ReportVm
{
    public Dictionary<string,decimal> InventoryValueByCurrency { get; set; } = new();
    public List<DepartmentAmountVm> DepartmentAmounts { get; set; } = new();
    public Dictionary<string,int> JobsByStatus { get; set; } = new();
    public List<StockItemRowVm> LowStockItems { get; set; } = new();
}

public class ChangePasswordVm
{
    [Required, DataType(DataType.Password)] public string CurrentPassword { get; set; } = string.Empty;
    [Required, DataType(DataType.Password), MinLength(8)] public string NewPassword { get; set; } = string.Empty;
    [Required, DataType(DataType.Password), Compare(nameof(NewPassword))] public string ConfirmPassword { get; set; } = string.Empty;
}

public class ProfileVm
{
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Roles { get; set; } = string.Empty;
    public string Departments { get; set; } = string.Empty;
}

public class StockTransferVm
{
    [Range(1, int.MaxValue)] public int StockItemId { get; set; }
    [Range(1, int.MaxValue)] public int FromWarehouseId { get; set; }
    [Range(1, int.MaxValue)] public int ToWarehouseId { get; set; }
    [Range(typeof(decimal), "0.001", "999999999")] public decimal Quantity { get; set; }
    [MaxLength(100)] public string? ReferenceNo { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
    public IEnumerable<SelectListItem> Items { get; set; } = Array.Empty<SelectListItem>();
    public IEnumerable<SelectListItem> Warehouses { get; set; } = Array.Empty<SelectListItem>();
}

public class CountrySelectionVm
{
    public List<Country> Countries { get; set; } = new();
    public Dictionary<int,int> ProjectCounts { get; set; } = new();
}

public class CountryProjectsVm
{
    public Country Country { get; set; } = new();
    public List<ProjectWorkspace> Workspaces { get; set; } = new();
}

public class ProjectWorkspaceDashboardVm
{
    public Project Project { get; set; } = new();
    public ProjectWorkspace Workspace { get; set; } = new();
    public Country Country { get; set; } = new();
    public List<ProjectSheet> Sheets { get; set; } = new();
    public int JobCount { get; set; }
    public int InventoryRequestCount { get; set; }
    public int PendingInventoryRequests { get; set; }
    public int CompletedJobs { get; set; }
}

public class SheetIndexVm
{
    public List<ProjectSheet> Sheets { get; set; } = new();
    public List<Project> Projects { get; set; } = new();
    public List<Department> Departments { get; set; } = new();
}

public class SheetDetailsVm
{
    public ProjectSheet Sheet { get; set; } = new();
    public List<SheetColumn> Columns { get; set; } = new();
    public List<SheetRow> Rows { get; set; } = new();
    public string? Search { get; set; }
    public int? LinkedImportExportSheetId { get; set; }
}

public class SheetRowFormVm
{
    public ProjectSheet Sheet { get; set; } = new();
    public SheetRow? Row { get; set; }
    public List<SheetColumn> Columns { get; set; } = new();
    public Dictionary<int,string?> Values { get; set; } = new();
    public List<Department> Departments { get; set; } = new();
    public List<Project> Projects { get; set; } = new();
    public List<Currency> Currencies { get; set; } = new();
}

public class InventoryRequestListVm
{
    public List<InventoryRequest> Requests { get; set; } = new();
    public int Total { get; set; }
    public int Pending { get; set; }
    public int Accepted { get; set; }
    public int Rejected { get; set; }
    public bool CanApprove { get; set; }
}

public class InventoryRequestFormVm
{
    [Required, MaxLength(160)] public string ItemName { get; set; } = string.Empty;
    [MaxLength(120)] public string? Category { get; set; }
    [Range(typeof(decimal), "0.001", "999999999")] public decimal Quantity { get; set; } = 1;
    [Required, MaxLength(30)] public string Unit { get; set; } = "EA";
    [Range(1, int.MaxValue)] public int DepartmentId { get; set; }
    public int? ProjectId { get; set; }
    [DataType(DataType.Date)] public DateTime RequestDate { get; set; } = DateTime.Today;
    [DataType(DataType.Date)] public DateTime? RequiredDate { get; set; }
    [Required, MaxLength(1200)] public string Purpose { get; set; } = string.Empty;
    [MaxLength(1500)] public string? Notes { get; set; }
    public IEnumerable<SelectListItem> Departments { get; set; } = Array.Empty<SelectListItem>();
    public IEnumerable<SelectListItem> Projects { get; set; } = Array.Empty<SelectListItem>();
}
