# START HERE — BGP Management System (.NET 8)

This is the actual ASP.NET Core MVC source project.

## Open in Visual Studio
1. Install Visual Studio 2022 with **ASP.NET and web development** workload and .NET 8 SDK.
2. Install SQL Server Express (default expected instance: `.\\SQLEXPRESS`).
3. Open `ContractorOperations.sln`.
4. Set `ContractorOperations.Web` as Startup Project.
5. Check `ContractorOperations.Web/appsettings.json` and update the SQL Server connection if needed.
6. Press **F5** / Run.
7. On first run, the database is created automatically and master data/roles/permissions are seeded.

## Initial development login
- Email: `admin@company.qa`
- Password: `ChangeMe123!`

Change this password before any real deployment.

## BGP v2 workflow
1. Log in.
2. The first screen is **Select Country** (Qatar is seeded initially).
3. Open Qatar and choose **Qatar 3D**. Use **Add More** for future projects.
4. Each project opens its own dashboard and receives a unique `PRJ-YYYY-####` ID.
5. Each project starts with **12 sheets**. Sheet 1 contains starter operational columns; every sheet supports dynamic rows/columns and auto row IDs.
6. Use **Add Sheet** to create additional sheets.
7. Use **Inventory Requests** for Pending / Accept / Reject / Issue / Return workflow.
8. Department-linked sheets can also be created from the Sheets module.

## Included functional areas
- Country & project workspace dashboard
- Project dashboard
- 12 starter project sheets + Add Sheet
- Dynamic rows / columns with automatic IDs
- Inventory request approval workflow
- Dashboard
- Jobs and line items
- Automatic job numbering
- Projects / Departments / Contractors / Units / Currencies
- Multiple users
- Role + user granular permissions
- Department-level data scope
- Inventory item/category/warehouse masters
- Receive / Issue / Adjust / Transfer stock
- Low-stock and out-of-stock indicators
- Reports / CSV exports
- Audit logs
- Settings

## Important production note
The package is source code and must be compiled, tested with the target SQL Server/hosting environment, and UAT-approved before live client deployment.

## If you previously ran v1.0 and received SQL Error 1785
The failed first run may have left a partially-created `ContractorOperationsDB`. Run `ContractorOperations.Web/database/RESET_INCOMPLETE_DB.sql` once (or delete that database in SSMS), then run the corrected application again. See `FIX_NOTES_v1.0.1.md`.


## BGP v2.1 update
See `CHANGELOG_BGP_v2.1.md` for department-wise job numbers, the 12 workbook-based Qatar 3D sheets, and the official BGP logo integration.
