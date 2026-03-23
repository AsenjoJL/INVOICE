using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace HazelInvoice.Areas.Identity.Pages.Account.Manage;

[Authorize]
public class IndexModel : PageModel
{
    private readonly UserManager<IdentityUser> _userManager;
    private readonly SignInManager<IdentityUser> _signInManager;

    public IndexModel(
        UserManager<IdentityUser> userManager,
        SignInManager<IdentityUser> signInManager)
    {
        _userManager = userManager;
        _signInManager = signInManager;
    }

    [TempData]
    public string? StatusMessage { get; set; }

    public string CurrentEmail { get; private set; } = string.Empty;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return Challenge();
        }

        await LoadAsync(user);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        if (user == null)
        {
            return Challenge();
        }

        await LoadAsync(user);

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var newEmail = (Input.NewEmail ?? string.Empty).Trim();
        var currentEmail = user.Email ?? string.Empty;

        if (string.Equals(newEmail, currentEmail, StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(Input.NewEmail), "The new email is the same as your current email.");
            return Page();
        }

        var passwordValid = await _userManager.CheckPasswordAsync(user, Input.CurrentPassword);
        if (!passwordValid)
        {
            ModelState.AddModelError(nameof(Input.CurrentPassword), "The current password is incorrect.");
            return Page();
        }

        var existingUser = await _userManager.FindByEmailAsync(newEmail);
        if (existingUser != null && existingUser.Id != user.Id)
        {
            ModelState.AddModelError(nameof(Input.NewEmail), "That email is already in use.");
            return Page();
        }

        user.Email = newEmail;
        user.UserName = newEmail;
        user.NormalizedEmail = _userManager.NormalizeEmail(newEmail);
        user.NormalizedUserName = _userManager.NormalizeName(newEmail);

        var result = await _userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }

            return Page();
        }

        await _signInManager.RefreshSignInAsync(user);
        StatusMessage = "Your email address has been updated.";
        return RedirectToPage();
    }

    private Task LoadAsync(IdentityUser user)
    {
        CurrentEmail = user.Email ?? user.UserName ?? string.Empty;
        return Task.CompletedTask;
    }

    public sealed class InputModel
    {
        [Required]
        [EmailAddress]
        [Display(Name = "New email")]
        public string NewEmail { get; set; } = string.Empty;

        [Required]
        [EmailAddress]
        [Display(Name = "Confirm new email")]
        [Compare(nameof(NewEmail), ErrorMessage = "The email and confirmation email do not match.")]
        public string ConfirmNewEmail { get; set; } = string.Empty;

        [Required]
        [DataType(DataType.Password)]
        [Display(Name = "Current password")]
        public string CurrentPassword { get; set; } = string.Empty;
    }
}
