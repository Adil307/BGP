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
            var sheet = sheets.FirstOrDefault(x => string.Equals(x.Name, template.Name, StringComparison.OrdinalIgnoreCase))
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
        foreach (var definition in template.Columns)
        {
            var existing = columns.FirstOrDefault(x => string.Equals(x.Name, definition.Name, StringComparison.OrdinalIgnoreCase));
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
            else if (!hasRows)
            {
                existing.ColumnType = definition.Type;
                existing.IsRequired = definition.Required;
                existing.Options = definition.Options;
                existing.SortOrder = order;
            }
            order++;
        }
        await _db.SaveChangesAsync();
    }

    private static IReadOnlyList<SheetTemplate> GetTemplates() => new List<SheetTemplate>
    {
        new("Service Business", new List<ColumnTemplate>
        {
            C("Request Person", required:true),
            C("Request Department", SheetColumnType.Dropdown, true, "__DEPARTMENTS__"),
            C("Person In Charge", required:true),
            C("Job Number", options:"__AUTO_JOB_NUMBER__"),
            C("Requisition No."),
            C("Service Approval No"),
            C("PO NO."),
            C("Contract No"),
            C("Subcontractor"),
            C("Local/Abroad", SheetColumnType.Dropdown, true, "Local,Abroad"),
            C("Service Description"),
            C("Export Job No"),
            C("Import Job No"),
            C("Invoice Number"),
            C("Invoice Amount", SheetColumnType.Currency),
            C("Invoice Currency", SheetColumnType.Dropdown, false, "__CURRENCIES__"),
            C("Exchange to USD", SheetColumnType.Currency)
        }),
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
            C("Frieght cost", SheetColumnType.Currency), C("Clearance (USD)", SheetColumnType.Currency),
            C("Request Dpt", SheetColumnType.Dropdown, false, "__DEPARTMENTS__"),
            C("Order Handled By"), C("Freight Handled By"), C("申请单号 Requisition No"), C("Contract/PO"), C("Shipping Invoice No."),
            C("Transportation", SheetColumnType.Dropdown, false, "Air,Land,Sea,Express"),
            C("Departure Port"), C("Destination"), C("ETD", SheetColumnType.Date), C("ETA", SheetColumnType.Date), C("Est.Clr Date", SheetColumnType.Date),
            C("Qty", SheetColumnType.Number), C("Unit"), C("PKG NO.", SheetColumnType.Number), C("PKG TYP"), C("GW", SheetColumnType.Number),
            C("Invoice Value", SheetColumnType.Currency), C("Currency", SheetColumnType.Dropdown, false, "__CURRENCIES__"), C("Handled By"),
            C("delivery location"), C("delivery date", SheetColumnType.Date), C("Attachment", SheetColumnType.File)
        }),
        new("Land transportation", Array.Empty<ColumnTemplate>()),
        new("Material Purchase", new List<ColumnTemplate>
        {
            C("Requisition No"), C("Request Department", SheetColumnType.Dropdown, false, "__DEPARTMENTS__"), C("Request By"), C("Person In Charge"),
            C("Progress", SheetColumnType.Status, false, "Pending,In Progress,Completed")
        }),
        new("Finacial", new List<ColumnTemplate>
        {
            C("SN", SheetColumnType.Number), C("Handling person"), C("Subcontractor"), C("Payment for"), C("Finacial No."),
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
