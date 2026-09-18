# Production Deployment Checklist

1. Replace the bootstrap administrator password and remove any demo users/data.
2. Set `Application:SeedDemoData` to `false`.
3. Use a dedicated SQL Server login with least-required privileges; do not use `sa` for production application traffic.
4. Move connection strings and passwords to environment variables / hosting secrets.
5. Enable HTTPS and redirect HTTP to HTTPS.
6. Configure daily database backups and test restore procedure.
7. Confirm Qatar client timezone and server timezone. The database stores audit timestamps in UTC; screens display local server/browser context.
8. Review all role permissions and user-specific overrides.
9. Assign each non-admin user to the correct department(s).
10. Verify master data: projects, departments, contractors, units, currencies, warehouses and stock categories.
11. Test duplicate job rule with client examples.
12. Test inventory negative-stock prevention and warehouse balances.
13. Test QAR and USD totals against manually calculated samples.
14. Test CSV exports in Excel.
15. Perform browser testing on current Chrome/Edge and mobile widths.
16. Change company name/tagline in Settings.
17. Run client UAT using the checklist in `UAT_CHECKLIST.md`.
18. After sign-off, create a release backup and record the deployed version.
