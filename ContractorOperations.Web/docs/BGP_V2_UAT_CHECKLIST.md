# BGP v2.0 — UAT Checklist

Use this checklist after opening the solution on a Windows machine with .NET 8 and SQL Server.

## Startup / branding
- [ ] Application starts without database errors.
- [ ] Login page and sidebar show **BGP**.
- [ ] Existing jobs, stock, users and permissions are still present after upgrading an existing database.

## Country / project flow
- [ ] After login, **Select Country** opens first.
- [ ] **Qatar** is visible.
- [ ] **Add More** can create another country (authorized user only).
- [ ] Opening Qatar shows **Qatar 3D**.
- [ ] Qatar 3D has a `PRJ-YYYY-####` ID.
- [ ] **Add More** can create another project and generates a different project ID.
- [ ] A newly created project opens its own dashboard.

## Project sheets
- [ ] Qatar 3D initially contains 12 sheets.
- [ ] A newly created project automatically gets 12 sheets.
- [ ] Each sheet has a different `SHT-YYYY-####` ID.
- [ ] Sheet 1 contains the operational starter columns.
- [ ] **Add Sheet** creates an additional sheet.
- [ ] Add Column supports Text, Number, Date, Time, Dropdown, Checkbox, Email, Phone, Currency, Percentage, File, User, Project and Status.
- [ ] Columns can be renamed, reordered and deleted.
- [ ] **Add Row** generates a unique `ROW-YYYY-####` ID.
- [ ] Rows can be edited, searched and deleted.
- [ ] File-type columns can upload and reopen a file.
- [ ] A sheet can be linked to a department and respects department data scope.

## Jobs
- [ ] **Add Job** from a project dashboard preselects that project.
- [ ] New jobs use `JOB-YYYY-####` numbering.
- [ ] Existing job CRUD, line items, filters and exports still work.

## Inventory requests
- [ ] A new request generates `INV-YYYY-####`.
- [ ] Request can be linked to Qatar 3D and a department.
- [ ] New request starts as Pending.
- [ ] Authorized inventory approver can Accept with approved quantity.
- [ ] Authorized inventory approver can Reject only with a reason.
- [ ] Accepted request can be marked Issued.
- [ ] Issued request can be marked Returned.
- [ ] Approval/rejection activity appears in Audit Log.

## Permissions
- [ ] Users without `Sheets.Manage` can view permitted sheets but cannot edit rows/columns.
- [ ] Users without `Inventory.Approve` cannot see Accept/Reject controls.
- [ ] Department-scoped users only see their permitted department data.

## Final client checks
- [ ] Replace the temporary text BGP mark with the supplied BGP logo.
- [ ] Confirm the exact final column definitions/names for Sheets 2–12 and update starter templates if required.
- [ ] Change the default administrator password before production deployment.
