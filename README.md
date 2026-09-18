# BGP — Project, Contractor Operations & Inventory Management System

ASP.NET Core MVC + Entity Framework Core + SQL Server business application for BGP project workspaces, contractor job operations, dynamic project/department sheets, inventory requests, stock inventory and granular user permissions.

## Main modules

- Country workspace selector (Qatar seeded initially)
- Project selector with Qatar 3D seeded initially and an **Add More** workflow
- Project-level dashboard with automatic `PRJ-YYYY-####` IDs
- 12 starter sheets for each new project plus **Add Sheet**
- Spreadsheet-style dynamic rows and columns (text, number, date, dropdown, status, file and more)
- Automatic sheet (`SHT-...`) and row (`ROW-...`) IDs
- Inventory request workflow with `INV-...` IDs, Pending / Accept / Reject / Issue / Return
- Department-specific custom sheets
- Secure login and multiple user accounts
- Role-based + user-specific granular permissions
- Department data scope (one or multiple departments per user)
- Automatic job number generation
- Project, department, contractor, currency and unit master data
- Job header + multiple line items
- Duplicate job and duplicate line prevention
- QAR / USD / other currency totals kept separate
- Job status, invoice and payment tracking
- Soft-delete recycle bin and restore for jobs
- Dashboard with jobs, status, department totals and inventory alerts
- Stock item master, warehouse master, category master
- Stock receipt, issue, warehouse transfer, positive/negative adjustment
- Negative-stock prevention
- Optional link between stock issue/receipt and job/contractor
- Low-stock / out-of-stock visibility
- Reports and CSV exports
- Audit log
- Company settings
- Responsive Qatar-client dashboard UI

## Technology

- .NET 8 / ASP.NET Core MVC
- Entity Framework Core 8
- ASP.NET Core Identity
- SQL Server / SQL Server Express
- Bootstrap 5 + custom responsive dashboard CSS
- Chart.js for dashboard charts

## Quick start on Windows

1. Install .NET 8 SDK, SQL Server Express, and Visual Studio 2022 or VS Code.
2. Open `ContractorOperations.sln`.
3. Update `ContractorOperations.Web/appsettings.json` connection string if your SQL Server instance is not `.\\SQLEXPRESS`.
4. Run the project. A new database is created automatically. For an existing database, the BGP v2 startup schema upgrader adds the new workspace, sheet and inventory-request tables without deleting existing operational data.
5. First login:
   - Email: `admin@company.qa`
   - Password: `ChangeMe123!`
6. Immediately create/change production credentials before client handover.

## Quick start with Docker

From this folder:

```bash
docker compose up --build
```

Then open `http://localhost:8080`.

**Before production:** change the SQL `sa` password in `docker-compose.yml` and use environment secrets rather than storing production passwords in files.

## Production checklist

Read `ContractorOperations.Web/docs/DEPLOYMENT_CHECKLIST.md` before deploying. Do not use demo seed data or default credentials in production.

## Notes

This package is a full source-code baseline matching the supplied dashboard and SRS scope. It should still be compiled and UAT-tested in the target hosting environment before client handover because hosting configuration, SQL Server version, SMTP/SSO requests and final business rules can vary by client.


## BGP v2.1 update
See `CHANGELOG_BGP_v2.1.md` for department-wise job numbers, the 12 workbook-based Qatar 3D sheets, and the official BGP logo integration.
