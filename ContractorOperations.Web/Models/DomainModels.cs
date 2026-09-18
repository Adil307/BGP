using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ContractorOperations.Web.Models;

public enum JobStatus { Draft, InProgress, Completed, OnHold, Cancelled }
public enum PaymentStatus { Uninvoiced, Invoiced, PartiallyPaid, Paid, OnHold }
public enum StockTransactionType { Receive, Issue, AdjustmentIn, AdjustmentOut }

public class ApplicationUser : IdentityUser
{
    [MaxLength(120)] public string FullName { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public ICollection<UserDepartment> UserDepartments { get; set; } = new List<UserDepartment>();
}

public class Project
{
    public int Id { get; set; }
    [Required, MaxLength(30)] public string Code { get; set; } = string.Empty;
    [Required, MaxLength(150)] public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public class Department
{
    public int Id { get; set; }
    [Required, MaxLength(30)] public string Code { get; set; } = string.Empty;
    [Required, MaxLength(120)] public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public class Contractor
{
    public int Id { get; set; }
    [Required, MaxLength(150)] public string Name { get; set; } = string.Empty;
    [MaxLength(120)] public string? ContactPerson { get; set; }
    [MaxLength(50)] public string? Phone { get; set; }
    [MaxLength(160)] public string? Email { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Currency
{
    public int Id { get; set; }
    [Required, MaxLength(3)] public string Code { get; set; } = string.Empty;
    [Required, MaxLength(60)] public string Name { get; set; } = string.Empty;
    [MaxLength(10)] public string? Symbol { get; set; }
    public bool IsActive { get; set; } = true;
}

public class Unit
{
    public int Id { get; set; }
    [Required, MaxLength(20)] public string Code { get; set; } = string.Empty;
    [Required, MaxLength(60)] public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}


public class JobSequence
{
    public int Id { get; set; }
    public int Year { get; set; }
    public int LastNumber { get; set; }
}

public class Job
{
    public int Id { get; set; }
    [Required, MaxLength(30)] public string JobNumber { get; set; } = string.Empty;
    [Required, MaxLength(64)] public string DeduplicationKey { get; set; } = string.Empty;
    public int ProjectId { get; set; }
    public Project? Project { get; set; }
    public int DepartmentId { get; set; }
    public Department? Department { get; set; }
    public int ContractorId { get; set; }
    public Contractor? Contractor { get; set; }
    [Required, MaxLength(1000)] public string Description { get; set; } = string.Empty;
    public DateTime JobDate { get; set; } = DateTime.Today;
    public DateTime? ServiceStart { get; set; }
    public DateTime? ServiceEnd { get; set; }
    public JobStatus Status { get; set; } = JobStatus.InProgress;
    [MaxLength(100)] public string? InvoiceNumber { get; set; }
    public DateTime? InvoiceDate { get; set; }
    public PaymentStatus PaymentStatus { get; set; } = PaymentStatus.Uninvoiced;
    [MaxLength(120)] public string? ProcessorName { get; set; }
    public string CreatedByUserId { get; set; } = string.Empty;
    public ApplicationUser? CreatedByUser { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
    [MaxLength(450)] public string? DeletedByUserId { get; set; }
    public ICollection<JobLine> Lines { get; set; } = new List<JobLine>();
    public ICollection<StockTransaction> StockTransactions { get; set; } = new List<StockTransaction>();
}

public class JobLine
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public Job? Job { get; set; }
    [Required, MaxLength(300)] public string Description { get; set; } = string.Empty;
    public int UnitId { get; set; }
    public Unit? Unit { get; set; }
    [Column(TypeName = "decimal(18,3)")] public decimal Quantity { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal UnitRate { get; set; }
    public int CurrencyId { get; set; }
    public Currency? Currency { get; set; }
    [NotMapped] public decimal Total => Quantity * UnitRate;
}

public class Permission
{
    public int Id { get; set; }
    [Required, MaxLength(100)] public string Key { get; set; } = string.Empty;
    [Required, MaxLength(120)] public string Name { get; set; } = string.Empty;
    [Required, MaxLength(60)] public string Group { get; set; } = string.Empty;
}

public class RolePermission
{
    public string RoleId { get; set; } = string.Empty;
    public IdentityRole? Role { get; set; }
    public int PermissionId { get; set; }
    public Permission? Permission { get; set; }
}

public class UserPermission
{
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }
    public int PermissionId { get; set; }
    public Permission? Permission { get; set; }
    public bool IsGranted { get; set; }
}

public class UserDepartment
{
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }
    public int DepartmentId { get; set; }
    public Department? Department { get; set; }
}

public class AuditLog
{
    public long Id { get; set; }
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(450)] public string? UserId { get; set; }
    [MaxLength(160)] public string? UserName { get; set; }
    [Required, MaxLength(80)] public string Action { get; set; } = string.Empty;
    [Required, MaxLength(80)] public string EntityName { get; set; } = string.Empty;
    [MaxLength(100)] public string? EntityId { get; set; }
    [MaxLength(2000)] public string? Details { get; set; }
    [MaxLength(64)] public string? IpAddress { get; set; }
}

public class StockCategory
{
    public int Id { get; set; }
    [Required, MaxLength(100)] public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}

public class Warehouse
{
    public int Id { get; set; }
    [Required, MaxLength(120)] public string Name { get; set; } = string.Empty;
    [MaxLength(250)] public string? Location { get; set; }
    public bool IsActive { get; set; } = true;
}

public class StockItem
{
    public int Id { get; set; }
    [Required, MaxLength(40)] public string ItemCode { get; set; } = string.Empty;
    [Required, MaxLength(160)] public string Name { get; set; } = string.Empty;
    public int StockCategoryId { get; set; }
    public StockCategory? StockCategory { get; set; }
    public int UnitId { get; set; }
    public Unit? Unit { get; set; }
    [Column(TypeName = "decimal(18,3)")] public decimal ReorderLevel { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal UnitCost { get; set; }
    public int CurrencyId { get; set; }
    public Currency? Currency { get; set; }
    public bool IsActive { get; set; } = true;
    public ICollection<StockBalance> Balances { get; set; } = new List<StockBalance>();
}

public class StockBalance
{
    public int Id { get; set; }
    public int StockItemId { get; set; }
    public StockItem? StockItem { get; set; }
    public int WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    [Column(TypeName = "decimal(18,3)")] public decimal QuantityOnHand { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class StockTransaction
{
    public long Id { get; set; }
    public int StockItemId { get; set; }
    public StockItem? StockItem { get; set; }
    public int WarehouseId { get; set; }
    public Warehouse? Warehouse { get; set; }
    public StockTransactionType Type { get; set; }
    [Column(TypeName = "decimal(18,3)")] public decimal Quantity { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal UnitCost { get; set; }
    public int CurrencyId { get; set; }
    public Currency? Currency { get; set; }
    public int? JobId { get; set; }
    public Job? Job { get; set; }
    public int? ContractorId { get; set; }
    public Contractor? Contractor { get; set; }
    [MaxLength(100)] public string? ReferenceNo { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
    public string CreatedByUserId { get; set; } = string.Empty;
    public ApplicationUser? CreatedByUser { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class SystemSetting
{
    public int Id { get; set; }
    [Required, MaxLength(160)] public string CompanyName { get; set; } = "Your Company";
    [MaxLength(160)] public string CompanyTagline { get; set; } = "Build | Operate | Grow";
    [MaxLength(160)] public string? SupportEmail { get; set; }
}
