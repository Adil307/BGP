# User Acceptance Test Checklist

## Authentication and permissions
- [ ] Administrator login works.
- [ ] Inactive user cannot log in.
- [ ] Department User cannot access unauthorized modules.
- [ ] Department User sees only assigned department jobs.
- [ ] User-specific Deny overrides a role Allow.
- [ ] User-specific Allow grants the selected action.

## Jobs
- [ ] Job number is generated automatically.
- [ ] Project / Department / Contractor dropdowns load active master data.
- [ ] Multiple line items can be added and removed.
- [ ] Quantity × Rate total is correct.
- [ ] QAR and USD are shown separately.
- [ ] Defined duplicate job is rejected.
- [ ] Exact duplicate line item is rejected.
- [ ] Job edit and detail screen work.
- [ ] Invoice/payment fields save correctly.
- [ ] Job CSV export opens correctly.

## Inventory
- [ ] Stock item can be created.
- [ ] Receipt increases the correct warehouse balance.
- [ ] Issue decreases the correct warehouse balance.
- [ ] Issue greater than available stock is rejected.
- [ ] Adjust + and Adjust - work correctly.
- [ ] Warehouse transfer moves stock between source and destination without allowing negative source stock.
- [ ] Job and contractor can be linked to a stock transaction.
- [ ] Low stock and out-of-stock status are correct.
- [ ] Inventory CSV export opens correctly.

## Administration
- [ ] New project / department / contractor can be added.
- [ ] Currency / unit / category / warehouse can be maintained.
- [ ] New user can be created and assigned a role/departments.
- [ ] Role permissions can be changed.
- [ ] User-specific permission overrides can be changed.
- [ ] Audit log records important changes.
- [ ] Company settings update dashboard branding.
