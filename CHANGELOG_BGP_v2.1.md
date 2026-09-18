# BGP v2.1

## Department-wise job numbering
- Main Jobs now generate job numbers by department and year.
- Format: `ACC-2026.001`, `ADM-2026.001`, `EQP-2026.001`, etc.
- Each department has its own independent sequence for each year.
- Legacy `JOB-YYYY-####` records are upgraded automatically at startup.
- The Service Business sheet uses the same department-wise sequence, preventing duplicate job numbers between Jobs and Service Business.

## 12 Qatar 3D starter sheets from the supplied workbook
1. Service Business
2. Contract Summary
3. Certificate Summary
4. Import and Export
5. Land transportation
6. Material Purchase
7. Finacial
8. Warehouse stock in and out
9. Critical Equipment
10. Vessel Detail
11. Fuel consumption
12. Vessel Network

The workbook-specified columns are pre-created where supplied. Sheets explicitly marked as empty remain empty so authorized users can add their own rows and columns.

## Workbook rules implemented
- Service Business: department dropdown, automatic department-wise Job Number, Local/Abroad dropdown, and Import/Export job links.
- When Service Business is Local, Export Job No and Import Job No fields are hidden and cleared.
- Import and Export: automatic `QAT3D-IMP-0001` / `QAT3D-EXP-0001` style shipment numbers, default In Progress status, workbook columns, and attachment upload.
- Contract Summary: Paid Till Date and Balance are calculated from matching Financial-sheet Contract No / Payment Amount entries; contracts ending within 30 days are highlighted.
- Dynamic Add Row / Add Column / rename / reorder / delete functionality remains available.

## Branding
- Added the supplied official BGP logo to the sidebar and login page.
- Footer version updated to 2.1.

## Database
- No new database tables or columns were required for v2.1.
- Existing v2.0 database schema can be used directly; normal application startup applies the data/template upgrade.
