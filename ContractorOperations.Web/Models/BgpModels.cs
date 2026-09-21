using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ContractorOperations.Web.Models;

public enum InventoryRequestStatus
{
    Pending,
    Accepted,
    Rejected,
    Issued,
    Returned
}

public enum SheetColumnType
{
    Text,
    Number,
    Date,
    Time,
    Dropdown,
    Checkbox,
    Email,
    Phone,
    Currency,
    Percentage,
    File,
    User,
    Project,
    Status
}

public class Country
{
    public int Id { get; set; }
    [Required, MaxLength(10)] public string Code { get; set; } = string.Empty;
    [Required, MaxLength(120)] public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ProjectWorkspace
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public Project? Project { get; set; }
    public int CountryId { get; set; }
    public Country? Country { get; set; }
    [Required, MaxLength(30)] public string UniqueId { get; set; } = string.Empty;
    [MaxLength(1500)] public string? Description { get; set; }
    [MaxLength(160)] public string? ProjectManager { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? Deadline { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal? Budget { get; set; }
    [MaxLength(20)] public string Priority { get; set; } = "Medium";
    [MaxLength(30)] public string Status { get; set; } = "Active";
    public int ProgressPercent { get; set; }
    [MaxLength(2000)] public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class BusinessSequence
{
    public int Id { get; set; }
    [Required, MaxLength(20)] public string Prefix { get; set; } = string.Empty;
    public int Year { get; set; }
    public int LastNumber { get; set; }
}

public class ProjectSheet
{
    public int Id { get; set; }
    [Required, MaxLength(30)] public string UniqueId { get; set; } = string.Empty;
    [Required, MaxLength(160)] public string Name { get; set; } = string.Empty;
    public int? ProjectId { get; set; }
    public Project? Project { get; set; }
    public int? DepartmentId { get; set; }
    public Department? Department { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<SheetColumn> Columns { get; set; } = new List<SheetColumn>();
    public ICollection<SheetRow> Rows { get; set; } = new List<SheetRow>();
}

public class SheetColumn
{
    public int Id { get; set; }
    public int ProjectSheetId { get; set; }
    public ProjectSheet? ProjectSheet { get; set; }
    [Required, MaxLength(120)] public string Name { get; set; } = string.Empty;
    public SheetColumnType ColumnType { get; set; } = SheetColumnType.Text;
    public int SortOrder { get; set; }
    public bool IsRequired { get; set; }
    [MaxLength(2000)] public string? Options { get; set; }
}

public class SheetRow
{
    public long Id { get; set; }
    public int ProjectSheetId { get; set; }
    public ProjectSheet? ProjectSheet { get; set; }
    [Required, MaxLength(40)] public string UniqueId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<SheetCell> Cells { get; set; } = new List<SheetCell>();
}

public class SheetCell
{
    public long Id { get; set; }
    public long SheetRowId { get; set; }
    public SheetRow? SheetRow { get; set; }
    public int SheetColumnId { get; set; }
    public SheetColumn? SheetColumn { get; set; }
    public string? Value { get; set; }
}


public class WorkflowNotification
{
    public long Id { get; set; }
    [Required, MaxLength(450)] public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }
    [Required, MaxLength(160)] public string Title { get; set; } = string.Empty;
    [Required, MaxLength(1000)] public string Message { get; set; } = string.Empty;
    [MaxLength(500)] public string? Url { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class InventoryRequest
{
    public long Id { get; set; }
    [Required, MaxLength(30)] public string RequestNumber { get; set; } = string.Empty;
    [Required, MaxLength(160)] public string ItemName { get; set; } = string.Empty;
    [MaxLength(120)] public string? Category { get; set; }
    [Column(TypeName = "decimal(18,3)")] public decimal Quantity { get; set; }
    [MaxLength(30)] public string Unit { get; set; } = "EA";
    public int DepartmentId { get; set; }
    public Department? Department { get; set; }
    public int? ProjectId { get; set; }
    public Project? Project { get; set; }
    [Required, MaxLength(450)] public string RequestedByUserId { get; set; } = string.Empty;
    public ApplicationUser? RequestedByUser { get; set; }
    public DateTime RequestDate { get; set; } = DateTime.Today;
    public DateTime? RequiredDate { get; set; }
    [Required, MaxLength(1200)] public string Purpose { get; set; } = string.Empty;
    [MaxLength(1500)] public string? Notes { get; set; }
    public InventoryRequestStatus Status { get; set; } = InventoryRequestStatus.Pending;
    [MaxLength(450)] public string? ApprovedByUserId { get; set; }
    public ApplicationUser? ApprovedByUser { get; set; }
    public DateTime? ApprovalDate { get; set; }
    [Column(TypeName = "decimal(18,3)")] public decimal? ApprovedQuantity { get; set; }
    [MaxLength(1000)] public string? RejectionReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
