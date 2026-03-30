using System.Threading.Tasks;
using Google.Apis.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace KeepBill.Controllers
{
    [AllowAnonymous]
    public class AuthController : Controller
    {
        private readonly UserManager<IdentityUser> _userManager;
        private readonly SignInManager<IdentityUser> _signInManager;
        private readonly IConfiguration _configuration;

        public AuthController(
            UserManager<IdentityUser> userManager,
            SignInManager<IdentityUser> signInManager,
            IConfiguration configuration)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _configuration = configuration;
        }

        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> GoogleOneTap(
            [FromForm] string credential,
            [FromForm(Name = "g_csrf_token")] string? csrfToken,
            [FromQuery] string? returnUrl = null)
        {
            if (string.IsNullOrWhiteSpace(credential))
            {
                return BadRequest("Credencial Google em falta.");
            }

            // Double submit cookie check recomendado pelo Google One Tap.
            if (!Request.Cookies.TryGetValue("g_csrf_token", out var csrfCookie) ||
                string.IsNullOrWhiteSpace(csrfToken) ||
                csrfCookie != csrfToken)
            {
                return BadRequest("CSRF token inválido.");
            }

            var clientId = _configuration["Authentication:Google:ClientId"];
            if (string.IsNullOrWhiteSpace(clientId))
            {
                return BadRequest("Google ClientId não configurado.");
            }

            GoogleJsonWebSignature.Payload payload;
            try
            {
                payload = await GoogleJsonWebSignature.ValidateAsync(
                    credential,
                    new GoogleJsonWebSignature.ValidationSettings
                    {
                        Audience = new[] { clientId }
                    });
            }
            catch
            {
                return BadRequest("Token Google inválido.");
            }

            if (string.IsNullOrWhiteSpace(payload.Email) || payload.EmailVerified != true)
            {
                return BadRequest("Email Google inválido ou não verificado.");
            }

            var user = await _userManager.FindByLoginAsync("Google", payload.Subject);
            if (user == null)
            {
                user = await _userManager.FindByEmailAsync(payload.Email);
            }

            if (user == null)
            {
                user = new IdentityUser
                {
                    UserName = payload.Email,
                    Email = payload.Email,
                    EmailConfirmed = true
                };

                var createResult = await _userManager.CreateAsync(user);
                if (!createResult.Succeeded)
                {
                    return BadRequest("Não foi possível criar utilizador local.");
                }
            }

            var hasGoogleLogin = await _userManager.FindByLoginAsync("Google", payload.Subject) != null;
            if (!hasGoogleLogin)
            {
                var addLogin = await _userManager.AddLoginAsync(
                    user,
                    new UserLoginInfo("Google", payload.Subject, "Google"));

                if (!addLogin.Succeeded)
                {
                    return BadRequest("Não foi possível associar login Google.");
                }
            }

            await _signInManager.SignInAsync(user, isPersistent: false);

            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return LocalRedirect(returnUrl);
            }

            return RedirectToAction("Index", "Home");
        }
    }
}
