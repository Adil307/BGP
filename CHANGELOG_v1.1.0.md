# Contractor Operations Suite - v1.1.0

Dashboard and usability polish release.

## Added
- Six-month job activity line chart.
- Jobs-by-department bar chart with clearer workload comparison.
- Job-status doughnut chart.
- Department cost comparison chart with each currency kept separate.
- Stock inventory health chart (healthy / low / out of stock).
- Department summary table with jobs, quantity and currency totals.
- Quick Setup & Master Data cards from the dashboard.
- Active project, department, contractor, currency and warehouse counts.
- Direct Add / Manage links beside Project, Department and Contractor on the Job form.
- Improved Master Data page with clearer guidance, counts, add panels and success messages.
- Responsive dashboard presentation improvements.

## Preserved
- .NET 8 ASP.NET Core MVC architecture.
- SQL Server / EF Core model.
- Identity authentication.
- Granular role/user permissions and department data scope.
- Automatic job numbering and duplicate protection.
- Job, invoice/payment, reporting, audit and stock inventory functionality.
- SQL Server cascade-path fix from v1.0.1.

## Important
The source was statically reviewed in the generation environment, but the environment does not contain the .NET SDK. Run `dotnet run` locally and complete UAT before production deployment.
