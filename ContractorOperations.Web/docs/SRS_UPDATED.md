# Software Requirements Specification (SRS)
## Contractor Operations, Job Management & Stock Inventory System

**Version:** 1.1  
**Platform:** ASP.NET Core Web Application  
**Primary deployment:** Qatar client environment  

## 1. Purpose

The system will replace spreadsheet-based contractor operation tracking with a controlled multi-user web application. It will record contractor jobs, quantities, rates, currencies, department ownership, invoice/payment information and stock inventory while providing management dashboards and permission-based access.

## 2. User roles and access

The system supports multiple user IDs. Initial roles are Administrator, Department Head, Department User and Read Only. Roles are not the only security layer: the Administrator can grant or deny individual permissions for each user and assign one or more departments to define data scope.

Granular permissions include Dashboard View; Jobs View/Create/Edit/Delete/Export; Inventory View/Create Item/Edit Item/Receive/Issue/Adjust/Export; Reports View/Export; Project/Department/Contractor/Master Data management; Users View/Manage; Permissions Manage; Audit View; Settings Manage; and All-Department Data Access.

Users without all-department access can only see jobs belonging to their assigned department(s). Server-side permission checks are mandatory; hiding a menu item alone is not considered security.

## 3. Job management

Each job receives an automatic job number in the format `JOB-YYYY-####`. Users select Project, Department and Contractor from administrator-managed dropdowns. Job description, job date, service dates, status, invoice, payment status and processor are recorded.

A job can contain multiple line items. Every line contains Description, Unit, Quantity, Unit Rate and Currency. Line total is calculated as Quantity × Unit Rate. The system keeps each currency separate instead of performing an undocumented conversion.

A duplicate job is rejected when the same Project + Department + Contractor + Job Date + Job Description already exists. Exact duplicate line items within the same job are also rejected.

## 4. Master data

Administrators can maintain Projects, Departments, Contractors, Currencies, Units, Stock Categories and Warehouses from the application. Existing values can be made inactive instead of being removed from history. Contractor contact person, phone and email can be stored.

Initial examples include QAT3D, QAT4D and TZ projects; Accounts, Logistics, Equipment, HSE and Admin departments; and QAR / USD currencies.

## 5. Dashboard

The dashboard follows the supplied UI direction and shows Total Jobs, Completed Jobs, In Progress, On Hold/Cancelled, Jobs by Department, Jobs by Status, Amount by Department separated by currency, Recent Jobs and quick actions.

Inventory summary cards show active stock items, low-stock items, out-of-stock items and warehouse count.

## 6. Stock inventory

The inventory module maintains item code, item name, category, unit, reorder level, unit cost, currency and active status. Stock can be held in multiple warehouses.

Supported movement types are Receive, Issue, Warehouse Transfer, Adjustment In and Adjustment Out. Each movement stores item, warehouse, quantity, unit cost, currency, optional job, optional contractor/supplier, reference number, notes, user and timestamp.

The system maintains stock balance by Item + Warehouse and prevents an issue or negative adjustment that would make the balance negative. Low-stock and out-of-stock items are highlighted based on reorder level.

## 7. Reports and summaries

Management can view department totals by currency, job count by status and low/out-of-stock items. Jobs and inventory can be exported to CSV. Department-based users only receive job information permitted by their data scope.

## 8. Audit and security

Jobs use soft delete: an authorized delete moves the job to a recycle bin, and authorized users can restore it. This protects operational history from accidental loss.


Important create, edit, delete, stock and security actions are recorded with user, date/time, entity, details and IP address. Authentication uses ASP.NET Core Identity. Password policy, account lockout, anti-forgery protection and server-side permission checks are included.

Production must use HTTPS, secure SQL credentials, backups and a changed administrator password.

## 9. Main screens

Login; Dashboard; Jobs List; Add Job; Edit Job; Job Details; Stock Inventory; Add/Edit Stock Item; Receive Stock; Issue Stock; Stock Adjustment; Stock Transactions; Reports; Projects; Departments; Contractors; Master Data; Users; Role Permissions; User Permission Overrides; Audit Log; Settings.

## 10. Acceptance criteria

The release is acceptable when a permitted user can log in, add a valid job, cannot add a defined duplicate, sees an automatic job number, sees correct line totals and separated currency totals, and is restricted to assigned department data unless granted all-department access.

Stock acceptance requires successful receipt, successful issue when quantity is available, rejection of an issue above available stock, accurate balance by warehouse, low-stock visibility and transaction history.

Administrator acceptance requires creation/editing of users and master data, role-level permission changes, user allow/deny overrides, department assignment and audit-log visibility.

## 11. Items to confirm with client before final production sign-off

- Final company name/logo and Qatar hosting details
- Final list of departments, projects and contractors
- Any approval workflow before a job becomes Completed
- Whether stock needs batch/serial number, expiry date or purchase-order modules
- Whether invoice attachments/PDF uploads are required
- Whether QAR/USD conversion should ever be performed and, if yes, what exchange-rate source/rule is approved
- Final report templates and any Arabic-language requirement
