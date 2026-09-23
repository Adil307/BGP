using ContractorOperations.Web.Data;
using ContractorOperations.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace ContractorOperations.Web.Services;

public interface IProjectSetupService
{
    Task EnsureInitialSheetsAsync(int projectId);
}

public class ProjectSetupService : IProjectSetupService
{
    private readonly ApplicationDbContext _db;
    private readonly IBusinessIdService _ids;

    private sealed record ColumnTemplate(string Name, SheetColumnType Type = SheetColumnType.Text, bool Required = false, string? Options = null);
    private sealed record SheetTemplate(string Name, IReadOnlyList<ColumnTemplate> Columns);

    public ProjectSetupService(ApplicationDbContext db, IBusinessIdService ids)
    {
        _db = db;
        _ids = ids;
    }

    public async Task EnsureInitialSheetsAsync(int projectId)
    {
        var templates = GetTemplates();
        var sheets = await _db.ProjectSheets
            .Where(x => x.ProjectId == projectId && x.IsActive)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .ToListAsync();

        for (var i = 0; i < templates.Count; i++)
        {
            var template = templates[i];
            var legacyName = $"Sheet {i + 1}";
            var aliases = template.Name.Equals("Financial", StringComparison.OrdinalIgnoreCase) ? new[] { "Finacial" }
                : template.Name.Equals("Requisition Approval", StringComparison.OrdinalIgnoreCase) ? new[] { "Service Approval" }
                : Array.Empty<string>();
            var sheet = sheets.FirstOrDefault(x => string.Equals(x.Name, template.Name, StringComparison.OrdinalIgnoreCase))
                ?? sheets.FirstOrDefault(x => aliases.Any(a => string.Equals(x.Name, a, StringComparison.OrdinalIgnoreCase)))
                ?? sheets.FirstOrDefault(x => string.Equals(x.Name, legacyName, StringComparison.OrdinalIgnoreCase))
                ?? sheets.FirstOrDefault(x => x.SortOrder == i + 1 && x.Name.StartsWith("Sheet ", StringComparison.OrdinalIgnoreCase));

            var wasLegacy = sheet != null && sheet.Name.StartsWith("Sheet ", StringComparison.OrdinalIgnoreCase);
            if (sheet == null)
            {
                sheet = new ProjectSheet
                {
                    ProjectId = projectId,
                    UniqueId = await _ids.NextAsync("SHT"),
                    Name = template.Name,
                    SortOrder = i + 1,
                    IsActive = true
                };
                _db.ProjectSheets.Add(sheet);
                await _db.SaveChangesAsync();
                sheets.Add(sheet);
            }
            else
            {
                sheet.Name = template.Name;
                sheet.SortOrder = i + 1;
                await _db.SaveChangesAsync();
            }

            await EnsureTemplateColumnsAsync(sheet, template, wasLegacy);
        }
    }

    private async Task EnsureTemplateColumnsAsync(ProjectSheet sheet, SheetTemplate template, bool wasLegacy)
    {
        var columns = await _db.SheetColumns.Where(x => x.ProjectSheetId == sheet.Id).OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToListAsync();
        var hasRows = await _db.SheetRows.AnyAsync(x => x.ProjectSheetId == sheet.Id);
        var legacyGeneric = columns.Count == 4 && new[] { "Description", "Status", "Date", "Notes" }.SequenceEqual(columns.Select(x => x.Name));

        // v2.0 created generic Sheet 1..12 placeholders. When no data was entered,
        // replace those placeholder columns with the exact workbook specification.
        if (!hasRows && (wasLegacy || legacyGeneric))
        {
            if (columns.Count > 0) _db.SheetColumns.RemoveRange(columns);
            await _db.SaveChangesAsync();
            columns.Clear();
        }

        if (template.Columns.Count == 0) return;

        var order = 1;
        foreach (var definition0 in template.Columns)
        {
            var definition = definition0;
            var normalizedName = new string(definition.Name.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            if (normalizedName.Contains("REQUISITIONNO") && string.IsNullOrWhiteSpace(definition.Options))
                definition = definition with { Type = SheetColumnType.Dropdown, Options = "__REQUISITIONS__" };
            var existing = columns.FirstOrDefault(x => string.Equals(x.Name, definition.Name, StringComparison.OrdinalIgnoreCase));
            if (existing == null && definition.Name.Equals("Financial No.", StringComparison.OrdinalIgnoreCase))
                existing = columns.FirstOrDefault(x => x.Name.Equals("Finacial No.", StringComparison.OrdinalIgnoreCase));
            if (existing == null && definition.Name.Equals("Freight Cost", StringComparison.OrdinalIgnoreCase))
                existing = columns.FirstOrDefault(x => x.Name.Equals("Frieght cost", StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                _db.SheetColumns.Add(new SheetColumn
                {
                    ProjectSheetId = sheet.Id,
                    Name = definition.Name,
                    ColumnType = definition.Type,
                    IsRequired = definition.Required,
                    Options = definition.Options,
                    SortOrder = order
                });
            }
            else
            {
                // Validation/reference metadata is safe to refresh even when the sheet already
                // contains data. This lets workflow improvements reach existing projects without
                // deleting or recreating any rows.
                existing.Name = definition.Name;
                existing.IsRequired = definition.Required;
                existing.Options = definition.Options;
                if (!hasRows || normalizedName.Contains("REQUISITIONNO") || normalizedName.Contains("SERVICEAPPROVALNO")) existing.ColumnType = definition.Type;
                existing.SortOrder = order;
            }
            order++;
        }
        await _db.SaveChangesAsync();
    }

    private static IReadOnlyList<SheetTemplate> GetTemplates() => new List<SheetTemplate>
    {
        // Service Business workflow
        new("Requisition", new List<ColumnTemplate>
        {
            C("Requisition No", options:"__AUTO_REQUISITION_NUMBER__"),
            C("Item No", SheetColumnType.Number, false, "__AUTO_REQUISITION_ITEM_NO__"),
            C("Requested By", SheetColumnType.Email, false, "__AUTO_CURRENT_USER_EMAIL__"),
            C("Request Department", SheetColumnType.Dropdown, true, "__DEPARTMENTS__"),
            C("Description", required:true),
            C("Unit", required:true),
            C("Qty", SheetColumnType.Number, true),
            C("Required Date", SheetColumnType.Date),
            C("Status", SheetColumnType.Status, false, "__AUTO_REQUISITION_STATUS__"),
            C("Approver / Manager", SheetColumnType.Email, false, "__COMPUTED_REQUISITION_APPROVER__"),
            C("Approval Comment", SheetColumnType.Text, false, "__COMPUTED_REQUISITION_APPROVAL_COMMENT__"),
            C("Decision By", SheetColumnType.Email, false, "__COMPUTED_REQUISITION_DECISION_BY__"),
            C("Decision Date", SheetColumnType.Date, false, "__COMPUTED_REQUISITION_DECISION_DATE__"),
            C("Request Date", SheetColumnType.Date, false, "__AUTO_TODAY__"),
            C("Notes")
        }),
        new("Requisition Approval", new List<ColumnTemplate>
        {
            C("Requisition Approval No", options:"__AUTO_REQUISITION_APPROVAL_NUMBER__"),
            C("Requisition No", SheetColumnType.Dropdown, true, "__REQUISITIONS__"),
            C("Requested By", SheetColumnType.Email, false, "__AUTO_CURRENT_USER_EMAIL__"),
            C("Approver / Manager", SheetColumnType.User, true, "__APPROVAL_MANAGERS__"),
            C("Request Department", SheetColumnType.Dropdown, true, "__DEPARTMENTS__"),
            C("Service Description", required:true),
            C("Scope / Justification", required:true),
            C("Service Type", SheetColumnType.Dropdown, true, "Repair,Maintenance,Calibration,Inspection,Rental,Other"),
            C("Estimated Amount", SheetColumnType.Currency),
            C("Currency", SheetColumnType.Dropdown, false, "__CURRENCIES__"),
            C("Request Date", SheetColumnType.Date, false, "__AUTO_TODAY__"),
            C("Approval Status", SheetColumnType.Status, false, "__AUTO_APPROVAL_STATUS__"),
            C("Manager Comment", SheetColumnType.Text, false, "__COMPUTED_MANAGER_COMMENT__"),
            C("Approved By", SheetColumnType.Email, false, "__COMPUTED_APPROVED_BY__"),
            C("Decision Date", SheetColumnType.Date, false, "__COMPUTED_DECISION_DATE__")
        }),
        new("Service Business", new List<ColumnTemplate>
        {
            C("Request Person", required:true),
            C("Request Department", SheetColumnType.Dropdown, false, "__COMPUTED_REQUISITION_DEPARTMENT__"),
            C("Person In Charge", required:true),
            C("Job Number", options:"__AUTO_JOB_NUMBER__"),
            C("Requisition No.", SheetColumnType.Dropdown, true, "__REQUISITIONS__"),
            C("Service Approval No", SheetColumnType.Dropdown, true, "__APPROVED_SERVICE_APPROVALS__"),
            C("Item No", SheetColumnType.Number, false, "__COMPUTED_REQUISITION_ITEM_NO__"),
            C("Service Description", SheetColumnType.Text, false, "__COMPUTED_REQUISITION_DESCRIPTION__"),
            C("Unit", SheetColumnType.Text, false, "__COMPUTED_REQUISITION_UNIT__"),
            C("Qty", SheetColumnType.Number, false, "__COMPUTED_REQUISITION_QTY__"),
            C("PO NO."),
            C("Contract No"),
            C("Subcontractor"),
            C("Local/Abroad", SheetColumnType.Dropdown, true, "Local,Abroad"),
            C("Export Job No"),
            C("Import Job No"),
            C("Invoice Number"),
            C("Invoice Amount", SheetColumnType.Currency),
            C("Invoice Currency", SheetColumnType.Dropdown, false, "__CURRENCIES__"),
            C("Exchange to USD", SheetColumnType.Currency)
        }),
        new("Purchase Order", new List<ColumnTemplate>
        {
            C("PO NO.", options:"__AUTO_PO_NUMBER__"),
            C("Requisition No", SheetColumnType.Dropdown, false, "__REQUISITIONS__"),
            C("Service Approval No", SheetColumnType.Dropdown, false, "__APPROVED_SERVICE_APPROVALS__"),
            C("Item No", SheetColumnType.Number),
            C("Job Number", SheetColumnType.Dropdown, true, "__SERVICE_JOBS__"),
            C("Vendor / Subcontractor", required:true),
            C("Quotation Reference"),
            C("PO Amount", SheetColumnType.Currency),
            C("Currency", SheetColumnType.Dropdown, false, "__CURRENCIES__"),
            C("PO Date", SheetColumnType.Date, false, "__AUTO_TODAY__"),
            C("Delivery / Service Terms"),
            C("Status", SheetColumnType.Status, false, "Draft,Sent,Acknowledged,Completed,Cancelled"),
            C("Notes")
        }),
        new("Service Logistics", new List<ColumnTemplate>
        {
            C("Requisition No", SheetColumnType.Dropdown, false, "__REQUISITIONS__"),
            C("Service Approval No", SheetColumnType.Dropdown, false, "__APPROVED_SERVICE_APPROVALS__"),
            C("Item No", SheetColumnType.Number),
            C("Job Number", SheetColumnType.Dropdown, true, "__SERVICE_JOBS__"),
            C("Mode", SheetColumnType.Dropdown, true, "Local,International"),
            C("Vendor / Subcontractor"),
            C("Delivery / Export Date", SheetColumnType.Date),
            C("Export Job No"),
            C("Follow-up Status", SheetColumnType.Status, false, "Pending,Delivered / Exported,In Service,Ready for Return,Received Back,Closed"),
            C("Received Back Date", SheetColumnType.Date),
            C("Import Job No"),
            C("Return Condition"),
            C("Follow-up Notes")
        }),
        new("Invoice Reception", new List<ColumnTemplate>
        {
            C("Requisition No", SheetColumnType.Dropdown, false, "__REQUISITIONS__"),
            C("Service Approval No", SheetColumnType.Dropdown, false, "__APPROVED_SERVICE_APPROVALS__"),
            C("Item No", SheetColumnType.Number),
            C("Job Number", SheetColumnType.Dropdown, true, "__SERVICE_JOBS__"),
            C("Invoice Number", required:true),
            C("Invoice Date", SheetColumnType.Date),
            C("Subcontractor"),
            C("Invoice Amount", SheetColumnType.Currency),
            C("Currency", SheetColumnType.Dropdown, false, "__CURRENCIES__"),
            C("Received Date", SheetColumnType.Date, false, "__AUTO_TODAY__"),
            C("Payment Status", SheetColumnType.Status, false, "Pending,Submitted to Finance,In Progress,Partially Paid,Paid,On Hold"),
            C("Payment Date", SheetColumnType.Date),
            C("Follow-up Notes")
        }),

        // Other operational sheets from the supplied workbook
        new("Contract Summary", new List<ColumnTemplate>
        {
            C("Contract No", required:true),
            C("Contractor name", required:true),
            C("Project", SheetColumnType.Project, false, "__PROJECTS__"),
            C("Contract amount", SheetColumnType.Currency),
            C("Start Date", SheetColumnType.Date),
            C("End Date", SheetColumnType.Date),
            C("Total Amount Paid till date", SheetColumnType.Currency, false, "__COMPUTED_CONTRACT_PAID__"),
            C("Contract Balance Amount", SheetColumnType.Currency, false, "__COMPUTED_CONTRACT_BALANCE__")
        }),
        new("Certificate Summary", Array.Empty<ColumnTemplate>()),
        new("Import and Export", new List<ColumnTemplate>
        {
            C("Shipment type (Import or Export)", SheetColumnType.Dropdown, true, "Import,Export"),
            C("Job Number", options:"__AUTO_SHIPMENT_JOB_NUMBER__"),
            C("Crew"), C("Import Type"),
            C("Status", SheetColumnType.Status, false, "In Progress,Finished"),
            C("Subcontractor"), C("AWB/BL"), C("Description"), C("PO"),
            C("Freight Cost", SheetColumnType.Currency), C("Clearance (USD)", SheetColumnType.Currency),
            C("Request Dpt", SheetColumnType.Dropdown, false, "__DEPARTMENTS__"),
            C("Order Handled By"), C("Freight Handled By"), C("申请单号 Requisition No", SheetColumnType.Dropdown, false, "__REQUISITIONS__"), C("Item No", SheetColumnType.Number), C("Contract/PO"), C("Shipping Invoice No."),
            C("Transportation", SheetColumnType.Dropdown, false, "Air,Land,Sea,Express"),
            C("Departure Port"), C("Destination"), C("ETD", SheetColumnType.Date), C("ETA", SheetColumnType.Date), C("Est.Clr Date", SheetColumnType.Date),
            C("Qty", SheetColumnType.Number), C("Unit"), C("PKG NO.", SheetColumnType.Number), C("PKG TYP"), C("GW", SheetColumnType.Number),
            C("Invoice Value", SheetColumnType.Currency), C("Currency", SheetColumnType.Dropdown, false, "__CURRENCIES__"), C("Handled By"),
            C("delivery location"), C("delivery date", SheetColumnType.Date), C("Attachment", SheetColumnType.File)
        }),
        new("Land transportation", Array.Empty<ColumnTemplate>()),
        new("Material Purchase", new List<ColumnTemplate>
        {
            C("Requisition No", SheetColumnType.Dropdown, false, "__REQUISITIONS__"), C("Item No", SheetColumnType.Number), C("Request Department", SheetColumnType.Dropdown, false, "__DEPARTMENTS__"), C("Request By"), C("Person In Charge"),
            C("Progress", SheetColumnType.Status, false, "Pending,In Progress,Completed")
        }),
        new("Financial", new List<ColumnTemplate>
        {
            C("SN", SheetColumnType.Number), C("Handling person"), C("Subcontractor"), C("Payment for"), C("Financial No."),
            C("Currency Of Invoice", SheetColumnType.Dropdown, false, "__CURRENCIES__"), C("Invoice Amount", SheetColumnType.Currency), C("Convert to USD", SheetColumnType.Currency),
            C("WHT", SheetColumnType.Currency), C("Payment Amount", SheetColumnType.Currency), C("Payment Methods"), C("payment terms"), C("Contract NO."),
            C("INVOICE DATE", SheetColumnType.Date), C("INVOICE NO."), C("PROJECT", SheetColumnType.Project, false, "__PROJECTS__"), C("Remark"), C("报账单号-TR NO"),
            C("报账单 审批状态", SheetColumnType.Status, false, "Pending,In Progress,Approved,Rejected"), C("服务/物料 类别编码"), C("MDG Code"), C("ERP Purchase No"),
            C("ERP Invoice No"), C("BPM No"), C("前方收到付款资料日期", SheetColumnType.Date), C("财务审核日期 Sent Date", SheetColumnType.Date),
            C("领导审批日期 Compete Sign Date", SheetColumnType.Date), C("付款日期 Payment Date", SheetColumnType.Date),
            C("付款状态 Payment Status", SheetColumnType.Status, false, "Pending,In Progress,Paid,On Hold"), C("备注"), C("科目")
        }),
        new("Warehouse stock in and out", Array.Empty<ColumnTemplate>()),
        new("Critical Equipment", new List<ColumnTemplate>
        {
            C("S/N", SheetColumnType.Number), C("Department", SheetColumnType.Dropdown, false, "__DEPARTMENTS__"), C("Equipment Name"), C("Model No"), C("Series No"),
            C("Person In Charge"), C("Department (2)", SheetColumnType.Dropdown, false, "__DEPARTMENTS__"), C("Equipment Condition"),
            C("New/Used", SheetColumnType.Dropdown, false, "New,Used")
        }),
        new("Vessel Detail", Array.Empty<ColumnTemplate>()),
        new("Fuel consumption", Array.Empty<ColumnTemplate>()),
        new("Vessel Network", Array.Empty<ColumnTemplate>())
    };

    private static ColumnTemplate C(string name, SheetColumnType type = SheetColumnType.Text, bool required = false, string? options = null)
        => new(name, type, required, options);
}
