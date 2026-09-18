# Contractor Operations Suite - Fix v1.0.1

## What was fixed
- Fixed SQL Server Error 1785 (multiple cascade paths) when creating `StockTransactions`.
- Stock transaction ledger foreign keys now use `DeleteBehavior.NoAction` so inventory history cannot be cascade-deleted through master records.
- Added a matching `JobLine` query filter for soft-deleted jobs.
- Renamed the permission action from `User` to `UserAccess` to remove the ASP.NET controller member-hiding warning.

## Important: one-time database reset
Your first failed run created `ContractorOperationsDB` only partially. Delete that incomplete database once before running the corrected project.

### Option A - SQL Server Management Studio
1. Stop the running application.
2. In SSMS, right-click `ContractorOperationsDB` > Delete.
3. Check **Close existing connections** if required.
4. Start the corrected application with `dotnet run`.

### Option B - sqlcmd / terminal
Run:

```powershell
sqlcmd -S .\SQLEXPRESS -E -Q "IF DB_ID('ContractorOperationsDB') IS NOT NULL BEGIN ALTER DATABASE [ContractorOperationsDB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [ContractorOperationsDB]; END"
```

Then:

```powershell
cd ContractorOperationsSuite\ContractorOperations.Web
dotnet run
```

The application will recreate the database and seed the configured data.
