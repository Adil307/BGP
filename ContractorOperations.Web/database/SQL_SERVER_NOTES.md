# SQL Server Notes

Default local connection string:

`Server=.\\SQLEXPRESS;Database=ContractorOperationsDB;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True`

The first release uses Entity Framework Core `EnsureCreated()` so a clean database is created automatically at first start. For a future schema-evolution phase, introduce EF Core migrations under source control before changing a live production database.

Recommended production setup: SQL Server 2022 or a supported Azure SQL / hosted SQL Server plan, dedicated application login, TLS encryption, daily backups and tested restore procedure.
