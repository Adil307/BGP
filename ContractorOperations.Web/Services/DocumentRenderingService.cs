using System.Net;
using ContractorOperations.Web.Data;
using ContractorOperations.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace ContractorOperations.Web.Services;

public sealed record DocumentRenderContext(
    long? GeneratedDocumentId = null,
    long? ServiceApprovalId = null,
    int? JobId = null,
    string? RequisitionNumber = null,
    int? ProjectId = null,
    string? CurrentUserId = null);

public interface IDocumentRenderingService
{
    Task<string> RenderAsync(string html, DocumentRenderContext context);
    Task<string> RenderGeneratedAsync(GeneratedCompanyDocument document, string? currentUserId = null);
}

public class DocumentRenderingService : IDocumentRenderingService
{
    private readonly ApplicationDbContext _db;

    public DocumentRenderingService(ApplicationDbContext db) => _db = db;

    public Task<string> RenderGeneratedAsync(GeneratedCompanyDocument document, string? currentUserId = null) =>
        RenderAsync(document.TemplateHtmlSnapshot, new DocumentRenderContext(
            document.Id,
            document.ServiceApprovalId,
            document.JobId,
            document.RequisitionNumber,
            document.ProjectId,
            currentUserId));

    public async Task<string> RenderAsync(string html, DocumentRenderContext context)
    {
        GeneratedCompanyDocument? generated = null;
        if (context.GeneratedDocumentId.HasValue)
        {
            generated = await _db.GeneratedCompanyDocuments.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Id == context.GeneratedDocumentId.Value);
        }

        ServiceApproval? approval = null;
        if (context.ServiceApprovalId.HasValue)
        {
            approval = await _db.ServiceApprovals.AsNoTracking()
                .Include(x => x.Project).Include(x => x.Department).Include(x => x.Currency)
                .Include(x => x.Stages)
                .FirstOrDefaultAsync(x => x.Id == context.ServiceApprovalId.Value);
        }

        Job? job = null;
        if (context.JobId.HasValue)
        {
            job = await _db.Jobs.AsNoTracking().IgnoreQueryFilters()
                .Include(x => x.Project).Include(x => x.Department).Include(x => x.Contractor)
                .Include(x => x.Lines).ThenInclude(x => x.Unit)
                .Include(x => x.Lines).ThenInclude(x => x.Currency)
                .FirstOrDefaultAsync(x => x.Id == context.JobId.Value && !x.IsDeleted);
        }

        var requisitionNumber = context.RequisitionNumber ?? approval?.RequisitionNumber ?? generated?.RequisitionNumber ?? string.Empty;
        var projectId = context.ProjectId ?? approval?.ProjectId ?? job?.ProjectId ?? generated?.ProjectId;
        var settings = await _db.SystemSettings.AsNoTracking().FirstOrDefaultAsync();

        var userIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (approval != null)
        {
            foreach (var id in approval.Stages.SelectMany(x => new[] { x.ApproverUserId, x.DecidedByUserId }))
                if (!string.IsNullOrWhiteSpace(id)) userIds.Add(id);
            if (!string.IsNullOrWhiteSpace(approval.CreatedByUserId)) userIds.Add(approval.CreatedByUserId);
        }
        if (job != null && !string.IsNullOrWhiteSpace(job.CreatedByUserId)) userIds.Add(job.CreatedByUserId);
        if (generated != null)
        {
            if (!string.IsNullOrWhiteSpace(generated.CreatedByUserId)) userIds.Add(generated.CreatedByUserId);
            if (!string.IsNullOrWhiteSpace(generated.FinalizedByUserId)) userIds.Add(generated.FinalizedByUserId);
        }
        if (!string.IsNullOrWhiteSpace(context.CurrentUserId)) userIds.Add(context.CurrentUserId);

        var users = await _db.Users.AsNoTracking().Where(x => userIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => string.IsNullOrWhiteSpace(x.FullName) ? (x.Email ?? x.UserName ?? x.Id) : x.FullName);

        var approvedBy = string.Empty;
        if (approval != null)
        {
            var finalStage = approval.Stages.Where(x => x.Status == ApprovalStageStatus.Approved && x.DecidedByUserId != null)
                .OrderByDescending(x => x.StageNumber).FirstOrDefault();
            if (finalStage?.DecidedByUserId != null) approvedBy = UserName(users, finalStage.DecidedByUserId);
        }

        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{{Logo}}"] = "<img class=\"doc-logo\" src=\"/images/bgp-logo.jpeg\" alt=\"BGP\" />",
            ["{{CompanyName}}"] = Encode(settings?.CompanyName),
            ["{{RequisitionNumber}}"] = Encode(requisitionNumber),
            ["{{JobNumber}}"] = Encode(job?.JobNumber),
            ["{{ServiceApprovalNumber}}"] = Encode(approval?.ApprovalNumber ?? (approval == null ? string.Empty : "Pending")),
            ["{{ServiceAmount}}"] = Encode(approval == null ? string.Empty : $"{approval.ServiceAmount:N2} {approval.Currency?.Code}"),
            ["{{ServiceDescription}}"] = Encode(approval?.Description),
            ["{{ServiceType}}"] = Encode(approval?.ServiceType),
            ["{{VendorSelectionMethod}}"] = Encode(approval?.VendorSelectionMethod),
            ["{{Date}}"] = Encode((approval?.ApprovalDate ?? job?.JobDate ?? DateTime.Today).ToString("dd MMM yyyy")),
            ["{{VendorName}}"] = Encode(job?.Contractor?.Name),
            ["{{ContractorName}}"] = Encode(job?.Contractor?.Name),
            ["{{Department}}"] = Encode(approval?.Department?.Name ?? job?.Department?.Name),
            ["{{Project}}"] = Encode(approval?.Project?.Name ?? job?.Project?.Name),
            ["{{ApprovedBy}}"] = Encode(approvedBy),
            ["{{CreatedBy}}"] = Encode(ResolveCreatedBy(generated, approval, job, users)),
            ["{{JobDescription}}"] = Encode(job?.Description),
            ["{{JobDate}}"] = Encode(job?.JobDate.ToString("dd MMM yyyy")),
            ["{{JobItemsTable}}"] = RenderJobItems(job),
            ["{{RequisitionItemsTable}}"] = await RenderRequisitionItemsAsync(projectId, requisitionNumber),
            ["{{CustomText1}}"] = Encode(generated?.CustomText1),
            ["{{CustomText2}}"] = Encode(generated?.CustomText2),
            ["{{CustomText3}}"] = Encode(generated?.CustomText3),
            ["{{CustomText4}}"] = Encode(generated?.CustomText4),
            ["{{CompanyStamp}}"] = await RenderCompanyStampAsync(generated, approval),
            ["{{MySignature}}"] = await RenderCurrentUserSignatureAsync(context.CurrentUserId)
        };

        await AddServiceApprovalStageReplacementsAsync(replacements, approval, users);
        await AddGeneratedSignatureReplacementsAsync(replacements, generated, users);

        foreach (var pair in replacements)
            html = html.Replace(pair.Key, pair.Value, StringComparison.OrdinalIgnoreCase);

        return html;
    }

    private async Task AddServiceApprovalStageReplacementsAsync(Dictionary<string, string> replacements, ServiceApproval? approval, Dictionary<string, string> users)
    {
        for (var i = 1; i <= 4; i++)
        {
            var stage = approval?.Stages.FirstOrDefault(x => x.StageNumber == i);
            replacements[$"{{{{Stage{i}Title}}}}"] = Encode(stage?.Title);
            replacements[$"{{{{Stage{i}Name}}}}"] = Encode(stage?.DecidedByUserId == null ? string.Empty : UserName(users, stage.DecidedByUserId));
            replacements[$"{{{{Stage{i}Decision}}}}"] = Encode(stage == null || stage.Status == ApprovalStageStatus.Pending ? string.Empty : stage.Status.ToString());
            replacements[$"{{{{Stage{i}Date}}}}"] = Encode(stage?.DecidedAt?.ToLocalTime().ToString("dd MMM yyyy HH:mm"));
            replacements[$"{{{{Stage{i}Comment}}}}"] = Encode(stage?.Comment);
            replacements[$"{{{{Stage{i}Signature}}}}"] = stage?.UserSignatureId == null ? string.Empty : SignatureImage(stage.UserSignatureId.Value, $"Stage {i} signature");
        }
        await Task.CompletedTask;
    }

    private async Task AddGeneratedSignatureReplacementsAsync(Dictionary<string, string> replacements, GeneratedCompanyDocument? document, Dictionary<string, string> users)
    {
        var signatures = document == null
            ? new List<GeneratedDocumentSignature>()
            : await _db.GeneratedDocumentSignatures.AsNoTracking().Where(x => x.GeneratedCompanyDocumentId == document.Id).ToListAsync();

        foreach (var signature in signatures)
            if (!string.IsNullOrWhiteSpace(signature.AppliedByUserId)) users.TryAdd(signature.AppliedByUserId, await GetUserNameAsync(signature.AppliedByUserId));

        for (var i = 1; i <= 4; i++)
        {
            var signature = signatures.FirstOrDefault(x => x.SlotNumber == i);
            replacements[$"{{{{Signer{i}Name}}}}"] = Encode(signature == null ? string.Empty : UserName(users, signature.AppliedByUserId));
            replacements[$"{{{{Signer{i}Date}}}}"] = Encode(signature?.AppliedAt.ToLocalTime().ToString("dd MMM yyyy HH:mm"));
            replacements[$"{{{{Signer{i}Signature}}}}"] = signature == null ? string.Empty : SignatureImage(signature.UserSignatureId, $"Signer {i} signature");
        }
    }

    private async Task<string> RenderCompanyStampAsync(GeneratedCompanyDocument? generated, ServiceApproval? approval)
    {
        string? referenceType = null; string? referenceId = null;
        if (generated != null) { referenceType = "CompanyDocument"; referenceId = generated.Id.ToString(); }
        else if (approval != null) { referenceType = "ServiceApproval"; referenceId = approval.Id.ToString(); }
        if (referenceType == null || referenceId == null) return string.Empty;

        var usage = await _db.DocumentAssetUsages.AsNoTracking().Include(x => x.OfficialDocumentAsset)
            .Where(x => !x.IsRemoved && x.ReferenceType == referenceType && x.ReferenceId == referenceId && x.OfficialDocumentAsset != null && x.OfficialDocumentAsset.AssetType == "CompanyStamp")
            .OrderByDescending(x => x.AppliedAt).FirstOrDefaultAsync();
        return usage?.OfficialDocumentAsset == null ? string.Empty : $"<img class=\"doc-stamp\" src=\"/Settings/OfficialAsset/{usage.OfficialDocumentAssetId}\" alt=\"Company stamp\" />";
    }

    private async Task<string> RenderCurrentUserSignatureAsync(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId)) return string.Empty;
        var signature = await _db.UserSignatures.AsNoTracking().Where(x => x.UserId == userId && x.IsActive).OrderByDescending(x => x.UpdatedAt).FirstOrDefaultAsync();
        return signature == null ? string.Empty : SignatureImage(signature.Id, "My signature");
    }

    private async Task<string> RenderRequisitionItemsAsync(int? projectId, string requisitionNumber)
    {
        if (!projectId.HasValue || string.IsNullOrWhiteSpace(requisitionNumber)) return string.Empty;
        var sheet = await _db.ProjectSheets.AsNoTracking().FirstOrDefaultAsync(x => x.ProjectId == projectId.Value && x.IsActive && x.Name == "Requisition");
        if (sheet == null) return string.Empty;
        var columns = await _db.SheetColumns.AsNoTracking().Where(x => x.ProjectSheetId == sheet.Id).ToListAsync();
        int? Column(string name) => columns.FirstOrDefault(x => x.Name == name)?.Id;
        var reqId = Column("Requisition No"); if (!reqId.HasValue) return string.Empty;
        var itemId = Column("Item No"); var descId = Column("Description"); var unitId = Column("Unit"); var qtyId = Column("Qty");
        var rows = await _db.SheetRows.AsNoTracking().Include(x => x.Cells)
            .Where(x => x.ProjectSheetId == sheet.Id && x.Cells.Any(c => c.SheetColumnId == reqId.Value && c.Value == requisitionNumber)).ToListAsync();
        if (rows.Count == 0) return string.Empty;
        var body = string.Join(string.Empty, rows.OrderBy(x => ParseItem(itemId.HasValue ? Cell(x, itemId.Value) : null)).Select(x =>
            $"<tr><td>{Encode(itemId.HasValue ? Cell(x, itemId.Value) : null)}</td><td>{Encode(descId.HasValue ? Cell(x, descId.Value) : null)}</td><td>{Encode(unitId.HasValue ? Cell(x, unitId.Value) : null)}</td><td>{Encode(qtyId.HasValue ? Cell(x, qtyId.Value) : null)}</td></tr>"));
        return $"<table class=\"doc-table\"><thead><tr><th>No.</th><th>Description</th><th>Unit</th><th>Qty</th></tr></thead><tbody>{body}</tbody></table>";
    }

    private static string RenderJobItems(Job? job)
    {
        if (job == null || job.Lines.Count == 0) return string.Empty;
        var body = string.Join(string.Empty, job.Lines.OrderBy(x => x.Id).Select((x, index) =>
            $"<tr><td>{index + 1}</td><td>{Encode(x.Description)}</td><td>{Encode(x.Unit?.Code)}</td><td>{x.Quantity:N3}</td><td>{x.UnitRate:N2}</td><td>{x.Total:N2} {Encode(x.Currency?.Code)}</td></tr>"));
        return $"<table class=\"doc-table\"><thead><tr><th>No.</th><th>Description</th><th>Unit</th><th>Qty</th><th>Unit Price</th><th>Total</th></tr></thead><tbody>{body}</tbody></table>";
    }

    private async Task<string> GetUserNameAsync(string userId) => await _db.Users.AsNoTracking().Where(x => x.Id == userId)
        .Select(x => string.IsNullOrWhiteSpace(x.FullName) ? (x.Email ?? x.UserName ?? x.Id) : x.FullName).FirstOrDefaultAsync() ?? userId;

    private static string ResolveCreatedBy(GeneratedCompanyDocument? generated, ServiceApproval? approval, Job? job, Dictionary<string, string> users)
    {
        var id = generated?.CreatedByUserId ?? approval?.CreatedByUserId ?? job?.CreatedByUserId;
        return string.IsNullOrWhiteSpace(id) ? string.Empty : UserName(users, id);
    }

    private static string SignatureImage(long id, string alt) => $"<img class=\"doc-signature\" src=\"/Account/UserSignature/{id}\" alt=\"{WebUtility.HtmlEncode(alt)}\" />";
    private static string UserName(Dictionary<string, string> users, string userId) => users.TryGetValue(userId, out var name) ? name : userId;
    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
    private static int ParseItem(string? value) => int.TryParse(value, out var number) ? number : int.MaxValue;
    private static string? Cell(SheetRow row, int columnId) => row.Cells.FirstOrDefault(x => x.SheetColumnId == columnId)?.Value?.Trim();
}
