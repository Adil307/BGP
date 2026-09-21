using ContractorOperations.Web.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ContractorOperations.Web.Controllers;

[Authorize]
public class NotificationsController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly UserManager<ContractorOperations.Web.Models.ApplicationUser> _userManager;

    public NotificationsController(ApplicationDbContext db, UserManager<ContractorOperations.Web.Models.ApplicationUser> userManager)
    {
        _db = db;
        _userManager = userManager;
    }

    public async Task<IActionResult> Index()
    {
        var userId = _userManager.GetUserId(User)!;
        var items = await _db.WorkflowNotifications.AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAt)
            .Take(200)
            .ToListAsync();
        return View(items);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkRead(long id)
    {
        var userId = _userManager.GetUserId(User)!;
        var notification = await _db.WorkflowNotifications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId);
        if (notification == null) return NotFound();
        notification.IsRead = true;
        await _db.SaveChangesAsync();
        if (!string.IsNullOrWhiteSpace(notification.Url)) return LocalRedirect(notification.Url);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkAllRead()
    {
        var userId = _userManager.GetUserId(User)!;
        var unread = await _db.WorkflowNotifications.Where(x => x.UserId == userId && !x.IsRead).ToListAsync();
        foreach (var item in unread) item.IsRead = true;
        await _db.SaveChangesAsync();
        return RedirectToAction(nameof(Index));
    }
}
