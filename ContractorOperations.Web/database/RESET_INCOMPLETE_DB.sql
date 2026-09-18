USE [master];
GO

IF DB_ID(N'ContractorOperationsDB') IS NOT NULL
BEGIN
    ALTER DATABASE [ContractorOperationsDB]
        SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE [ContractorOperationsDB];
END
GO
