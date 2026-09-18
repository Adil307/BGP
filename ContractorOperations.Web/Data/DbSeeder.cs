using ContractorOperations.Web.Models;
using ContractorOperations.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace ContractorOperations.Web.Data;

public static class DbSeeder
{
    public static readonly string[] PermissionKeys =
    {
        "Dashboard.View",
        "Jobs.View","Jobs.Create","Jobs.Edit","Jobs.Delete","Jobs.Export",
        "Inventory.View","Inventory.CreateItem","Inventory.EditItem","Inventory.Receive","Inventory.Issue","Inventory.Adjust","Inventory.Transfer","Inventory.Export","Inventory.Requests","Inventory.Approve",
        "Sheets.View","Sheets.Manage",
        "Reports.View","Reports.Export",
        "Projects.Manage","Departments.Manage","Contractors.Manage","MasterData.Manage",
        "Users.View","Users.Manage","Permissions.Manage",
        "Audit.View","Settings.Manage","Data.AllDepartments"
    };

    public static async Task SeedAsync(IServiceProvider services, IConfiguration configuration)
    {
        var db = services.GetRequiredService<ApplicationDbContext>();
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
        var jobNumberService = services.GetRequiredService<IJobNumberService>();

        foreach (var roleName in new[] { "Administrator", "Super Admin", "Project Manager", "Inventory Manager", "Department Head", "Department User", "Read Only", "Viewer" })
        {
            if (!await roleManager.RoleExistsAsync(roleName))
                await roleManager.CreateAsync(new IdentityRole(roleName));
        }

        var permissionDefs = new (string Key, string Name, string Group)[]
        {
            ("Dashboard.View","View dashboard","Dashboard"),
            ("Jobs.View","View jobs","Jobs"),("Jobs.Create","Create jobs","Jobs"),("Jobs.Edit","Edit jobs","Jobs"),("Jobs.Delete","Delete jobs","Jobs"),("Jobs.Export","Export jobs","Jobs"),
            ("Inventory.View","View stock inventory","Inventory"),("Inventory.CreateItem","Create stock items","Inventory"),("Inventory.EditItem","Edit stock items","Inventory"),("Inventory.Receive","Receive stock","Inventory"),("Inventory.Issue","Issue stock","Inventory"),("Inventory.Adjust","Adjust stock","Inventory"),("Inventory.Transfer","Transfer stock between warehouses","Inventory"),("Inventory.Export","Export inventory","Inventory"),
            ("Inventory.Requests","Create and view inventory requests","Inventory"),("Inventory.Approve","Accept or reject inventory requests","Inventory"),
            ("Sheets.View","View project and department sheets","Sheets"),("Sheets.Manage","Create and edit sheets, rows and columns","Sheets"),
            ("Reports.View","View reports","Reports"),("Reports.Export","Export reports","Reports"),
            ("Projects.Manage","Manage projects","Master Data"),("Departments.Manage","Manage departments","Master Data"),("Contractors.Manage","Manage contractors","Master Data"),("MasterData.Manage","Manage currencies, units, categories and warehouses","Master Data"),
            ("Users.View","View users","Security"),("Users.Manage","Manage users","Security"),("Permissions.Manage","Manage granular permissions","Security"),
            ("Audit.View","View audit log","Audit"),("Settings.Manage","Manage system settings","Settings"),("Data.AllDepartments","Access data from all departments","Data Scope")
        };

        foreach (var p in permissionDefs)
        {
            if (!await db.Permissions.AnyAsync(x => x.Key == p.Key))
                db.Permissions.Add(new Permission { Key = p.Key, Name = p.Name, Group = p.Group });
        }
        await db.SaveChangesAsync();

        var adminRole = await roleManager.FindByNameAsync("Administrator");
        if (adminRole != null)
        {
            var allPermissionIds = await db.Permissions.Select(x => x.Id).ToListAsync();
            var current = await db.RolePermissions.Where(x => x.RoleId == adminRole.Id).Select(x => x.PermissionId).ToListAsync();
            foreach (var permissionId in allPermissionIds.Except(current))
                db.RolePermissions.Add(new RolePermission { RoleId = adminRole.Id, PermissionId = permissionId });
            await db.SaveChangesAsync();
        }

        var superAdminRole = await roleManager.FindByNameAsync("Super Admin");
        if (superAdminRole != null)
        {
            var allPermissionIds = await db.Permissions.Select(x => x.Id).ToListAsync();
            var current = await db.RolePermissions.Where(x => x.RoleId == superAdminRole.Id).Select(x => x.PermissionId).ToListAsync();
            foreach (var permissionId in allPermissionIds.Except(current))
                db.RolePermissions.Add(new RolePermission { RoleId = superAdminRole.Id, PermissionId = permissionId });
            await db.SaveChangesAsync();
        }

        var projectManagerRole = await roleManager.FindByNameAsync("Project Manager");
        var inventoryManagerRole = await roleManager.FindByNameAsync("Inventory Manager");
        var headRole = await roleManager.FindByNameAsync("Department Head");
        var userRole = await roleManager.FindByNameAsync("Department User");
        var readRole = await roleManager.FindByNameAsync("Read Only");
        var viewerRole = await roleManager.FindByNameAsync("Viewer");
        await GrantRoleAsync(db, projectManagerRole, "Dashboard.View","Jobs.View","Jobs.Create","Jobs.Edit","Jobs.Export","Projects.Manage","Sheets.View","Sheets.Manage","Inventory.View","Inventory.Requests","Reports.View","Reports.Export");
        await GrantRoleAsync(db, inventoryManagerRole, "Dashboard.View","Inventory.View","Inventory.CreateItem","Inventory.EditItem","Inventory.Receive","Inventory.Issue","Inventory.Adjust","Inventory.Transfer","Inventory.Export","Inventory.Requests","Inventory.Approve","Sheets.View","Reports.View","Reports.Export");
        await GrantRoleAsync(db, headRole, "Dashboard.View","Jobs.View","Jobs.Create","Jobs.Edit","Jobs.Export","Inventory.View","Inventory.Receive","Inventory.Issue","Inventory.Transfer","Inventory.Requests","Inventory.Approve","Sheets.View","Sheets.Manage","Reports.View","Reports.Export");
        await GrantRoleAsync(db, userRole, "Dashboard.View","Jobs.View","Jobs.Create","Inventory.View","Inventory.Requests","Sheets.View");
        await GrantRoleAsync(db, readRole, "Dashboard.View","Jobs.View","Inventory.View","Sheets.View","Reports.View");
        await GrantRoleAsync(db, viewerRole, "Dashboard.View","Jobs.View","Inventory.View","Sheets.View","Reports.View");

        if (!await db.Departments.AnyAsync())
        {
            db.Departments.AddRange(
                new Department { Code = "ACC", Name = "Accounts" },
                new Department { Code = "LOG", Name = "Logistics" },
                new Department { Code = "EQP", Name = "Equipment" },
                new Department { Code = "HSE", Name = "HSE" },
                new Department { Code = "ADM", Name = "Admin" });
        }
        if (!await db.Projects.AnyAsync())
        {
            db.Projects.Add(new Project { Code = "QAT3D", Name = "Qatar 3D", IsActive = true });
        }
        if (!await db.Currencies.AnyAsync())
        {
            db.Currencies.AddRange(
                new Currency { Code = "QAR", Name = "Qatari Riyal", Symbol = "QAR" },
                new Currency { Code = "USD", Name = "US Dollar", Symbol = "$" });
        }
        if (!await db.Units.AnyAsync())
        {
            db.Units.AddRange(
                new Unit { Code = "EA", Name = "Each" },
                new Unit { Code = "HR", Name = "Hour" },
                new Unit { Code = "DAY", Name = "Day" },
                new Unit { Code = "M", Name = "Meter" },
                new Unit { Code = "KG", Name = "Kilogram" },
                new Unit { Code = "LOT", Name = "Lot" });
        }
        if (!await db.Contractors.AnyAsync())
        {
            db.Contractors.AddRange(
                new Contractor { Name = "ABC Trading" },
                new Contractor { Name = "XYZ Services" },
                new Contractor { Name = "Doha Equipment" },
                new Contractor { Name = "Safety First" });
        }
        if (!await db.StockCategories.AnyAsync())
        {
            db.StockCategories.AddRange(
                new StockCategory { Name = "General" },
                new StockCategory { Name = "Safety" },
                new StockCategory { Name = "Tools & Equipment" },
                new StockCategory { Name = "Consumables" });
        }
        if (!await db.Warehouses.AnyAsync())
            db.Warehouses.Add(new Warehouse { Name = "Main Warehouse", Location = "Qatar" });
        var settings = await db.SystemSettings.FirstOrDefaultAsync();
        if (settings == null)
        {
            db.SystemSettings.Add(new SystemSetting
            {
                CompanyName = "BGP",
                CompanyTagline = "Project & Operations Management"
            });
        }
        else
        {
            settings.CompanyName = "BGP";
            settings.CompanyTagline = "Project & Operations Management";
        }

        await db.SaveChangesAsync();

        // Upgrade old global JOB-YYYY-#### identifiers to department-wise job
        // numbers (ACC-2026.001, ADM-2026.001, etc.) without touching jobs
        // that already use the new format.
        await UpgradeLegacyJobNumbersAsync(db, jobNumberService);

        // BGP workspace starts with Qatar and one project: Qatar 3D.
        var qatar = await db.Countries.FirstOrDefaultAsync(x => x.Code == "QA");
        if (qatar == null)
        {
            qatar = new Country { Code = "QA", Name = "Qatar", IsActive = true };
            db.Countries.Add(qatar);
            await db.SaveChangesAsync();
        }

        var qatar3d = await db.Projects.FirstOrDefaultAsync(x => x.Code == "QAT3D" || x.Name == "Qatar 3D" || x.Name == "QAT3D");
        if (qatar3d == null)
        {
            qatar3d = new Project { Code = "QAT3D", Name = "Qatar 3D", IsActive = true };
            db.Projects.Add(qatar3d);
            await db.SaveChangesAsync();
        }
        else if (qatar3d.Name == "QAT3D")
        {
            qatar3d.Name = "Qatar 3D";
            await db.SaveChangesAsync();
        }

        var idService = services.GetRequiredService<ContractorOperations.Web.Services.IBusinessIdService>();
        var setupService = services.GetRequiredService<ContractorOperations.Web.Services.IProjectSetupService>();
        var workspace = await db.ProjectWorkspaces.FirstOrDefaultAsync(x => x.ProjectId == qatar3d.Id);
        if (workspace == null)
        {
            workspace = new ProjectWorkspace
            {
                ProjectId = qatar3d.Id,
                CountryId = qatar.Id,
                UniqueId = await idService.NextAsync("PRJ"),
                Status = "Active",
                Priority = "Medium"
            };
            db.ProjectWorkspaces.Add(workspace);
            await db.SaveChangesAsync();
        }
        await setupService.EnsureInitialSheetsAsync(qatar3d.Id);

        var adminEmail = "admin@company.qa";
        var admin = await userManager.FindByEmailAsync(adminEmail);
        if (admin == null)
        {
            admin = new ApplicationUser
            {
                UserName = adminEmail,
                Email = adminEmail,
                EmailConfirmed = true,
                FullName = "System Administrator",
                IsActive = true
            };
            var result = await userManager.CreateAsync(admin, "ChangeMe123!");
            if (result.Succeeded) await userManager.AddToRoleAsync(admin, "Administrator");
        }

        if (configuration.GetValue<bool>("Application:SeedDemoData"))
            await SeedDemoDataAsync(db, admin?.Id ?? string.Empty, jobNumberService);
    }

    private static async Task GrantRoleAsync(ApplicationDbContext db, IdentityRole? role, params string[] keys)
    {
        if (role == null) return;
        var ids = await db.Permissions.Where(x => keys.Contains(x.Key)).Select(x => x.Id).ToListAsync();
        var current = await db.RolePermissions.Where(x => x.RoleId == role.Id).Select(x => x.PermissionId).ToListAsync();
        foreach (var id in ids.Except(current)) db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = id });
        await db.SaveChangesAsync();
    }

    private static async Task UpgradeLegacyJobNumbersAsync(ApplicationDbContext db, IJobNumberService jobNumberService)
    {
        var legacyJobs = await db.Jobs.IgnoreQueryFilters()
            .Where(x => x.JobNumber.StartsWith("JOB-"))
            .OrderBy(x => x.JobDate).ThenBy(x => x.Id)
            .ToListAsync();

        foreach (var job in legacyJobs)
        {
            job.JobNumber = await jobNumberService.NextAsync(job.DepartmentId);
            job.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    private static async Task SeedDemoDataAsync(ApplicationDbContext db, string adminId, IJobNumberService jobNumberService)
    {
        if (string.IsNullOrWhiteSpace(adminId) || await db.Jobs.AnyAsync()) return;
        var departments = await db.Departments.ToDictionaryAsync(x => x.Name, x => x.Id);
        var projects = await db.Projects.ToListAsync();
        var contractors = await db.Contractors.ToListAsync();
        var currencies = await db.Currencies.ToDictionaryAsync(x => x.Code, x => x.Id);
        var unit = await db.Units.FirstAsync();

        var demo = new[]
        {
            new { Desc="Supply and install shelves", Dept="Logistics", Qty=10m, Rate=500m, Cur="QAR", Status=JobStatus.InProgress },
            new { Desc="Office partition work", Dept="Accounts", Qty=1m, Rate=12000m, Cur="USD", Status=JobStatus.Completed },
            new { Desc="Generator servicing", Dept="Equipment", Qty=2m, Rate=1600m, Cur="QAR", Status=JobStatus.InProgress },
            new { Desc="Fire extinguisher refilling", Dept="HSE", Qty=15m, Rate=300m, Cur="QAR", Status=JobStatus.Completed }
        };

        var n = 1;
        foreach (var x in demo)
        {
            var job = new Job
            {
                JobNumber = await jobNumberService.NextAsync(departments[x.Dept]), ProjectId = projects[(n-1)%projects.Count].Id,
                DepartmentId = departments[x.Dept], ContractorId = contractors[(n-1)%contractors.Count].Id,
                Description = x.Desc, JobDate = DateTime.Today.AddDays(-n), Status = x.Status,
                DeduplicationKey = Dedup(projects[(n-1)%projects.Count].Id, departments[x.Dept], contractors[(n-1)%contractors.Count].Id, DateTime.Today.AddDays(-n), x.Desc),
                CreatedByUserId = adminId, CreatedAt = DateTime.UtcNow.AddDays(-n), UpdatedAt = DateTime.UtcNow.AddDays(-n)
            };
            job.Lines.Add(new JobLine { Description = x.Desc, UnitId = unit.Id, Quantity = x.Qty, UnitRate = x.Rate, CurrencyId = currencies[x.Cur] });
            db.Jobs.Add(job); n++;
        }
        await db.SaveChangesAsync();
    }

    private static string Dedup(int projectId, int departmentId, int contractorId, DateTime jobDate, string description)
    {
        var raw = $"{projectId}|{departmentId}|{contractorId}|{jobDate:yyyy-MM-dd}|{description.Trim().ToUpperInvariant()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

}
