# BGP v2.0 — Requested Workspace Upgrade

## Branding
- Software name changed to **BGP**.
- Text-based BGP brand mark is used until the final logo is supplied.

## Country and project navigation
- Login/default route opens **Select Country**.
- **Qatar** is seeded initially.
- **Add More** can create additional countries.
- Qatar contains the initial project **Qatar 3D**.
- **Add More** can create additional projects.
- Every workspace project receives an automatic unique ID such as `PRJ-2026-0001`.

## Project dashboard and sheets
- Every project opens a dedicated project dashboard.
- New projects automatically receive **12 starter sheets** (`Sheet 1` … `Sheet 12`).
- Every sheet receives an automatic ID such as `SHT-2026-0001`.
- Every sheet row receives an automatic ID such as `ROW-2026-0001`.
- Sheet 1 is preloaded with operational/job columns from the existing system.
- Sheets 2–12 contain starter Description / Status / Date / Notes columns and can be changed dynamically.
- **Add Sheet** creates additional project or department sheets.
- Users with sheet-management permission can add, rename, reorder and remove columns; add/edit/delete rows; search rows; and create common data types including Text, Number, Date, Time, Dropdown, Checkbox, Email, Phone, Currency, Percentage, File, User, Project and Status.

## Inventory requests
- Added project/department inventory requests with automatic IDs such as `INV-2026-0001`.
- Workflow: Pending → Accepted or Rejected → Issued → Returned.
- Acceptance records approved quantity, approving user and date.
- Rejection requires a reason.
- Full actions are written to the existing audit log.

## Permissions / roles
- Added `Sheets.View`, `Sheets.Manage`, `Inventory.Requests`, and `Inventory.Approve`.
- Added/seeded Project Manager, Inventory Manager, Super Admin and Viewer role options while retaining existing roles.

## Database compatibility
- New EF Core entities and relationships were added for Countries, ProjectWorkspaces, BusinessSequences, ProjectSheets, SheetColumns, SheetRows, SheetCells and InventoryRequests.
- `DatabaseSchemaUpgrade` runs at startup and creates these tables when an existing v1 database is detected. It does not delete existing project/job/stock data.

## Notes
- The final BGP logo has not been embedded because it was not included yet. Replace the text BGP mark after the logo is supplied.
- Exact business columns for Sheets 2–12 were not supplied in the requested change list, so they are intentionally editable starter sheets.
