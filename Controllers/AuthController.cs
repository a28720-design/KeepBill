using System.Threading.Tasks;
using Google.Apis.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace KeepBill.Controllers
{
    [AllowAnonymous]
    public class AuthController : Controller
    {
        private readonly UserManager<IdentityUser> _userManager;
        private readonly SignInManager<IdentityUser> _signInManager;
        private readonly IConfiguration _configuration;
        private readonly ILogger<AuthController> _logger;

        public AuthController(
            UserManager<IdentityUser> userManager,
            SignInManager<IdentityUser> signInManager,
            IConfiguration configuration,
            ILogger<AuthController> logger)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _configuration = configuration;
            _logger = logger;
        }

        [HttpGet]
        public IActionResult GoogleOneTap(string? returnUrl = null)
        {
            TempData["Toast"] = "Usa o botao 'Entrar com Google' na pagina de login.";
            return RedirectToPage("/Account/Login", new { area = "Identity", returnUrl });
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
                TempData["Toast"] = "Falha no login Google: credencial em falta.";
                return RedirectToPage("/Account/Login", new { area = "Identity", returnUrl });
            }

            var hasCookie = Request.Cookies.TryGetValue("g_csrf_token", out var csrfCookie);
            var hasToken = !string.IsNullOrWhiteSpace(csrfToken);
            if (hasCookie && hasToken && csrfCookie != csrfToken)
            {
                TempData["Toast"] = "Falha no login Google: token CSRF invalido.";
                return RedirectToPage("/Account/Login", new { area = "Identity", returnUrl });
            }

            if (!hasCookie || !hasToken)
            {
                _logger.LogWarning("Google One Tap arrived without full CSRF pair. Cookie present: {HasCookie}, token present: {HasToken}.", hasCookie, hasToken);
            }

            var clientId = _configuration["Authentication:Google:ClientId"];
            if (string.IsNullOrWhiteSpace(clientId))
            {
                TempData["Toast"] = "Google ClientId nao configurado.";
                return RedirectToPage("/Account/Login", new { area = "Identity", returnUrl });
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
                TempData["Toast"] = "Token Google invalido.";
                return RedirectToPage("/Account/Login", new { area = "Identity", returnUrl });
            }

            if (string.IsNullOrWhiteSpace(payload.Email) || payload.EmailVerified != true)
            {
                TempData["Toast"] = "Email Google invalido ou nao verificado.";
                return RedirectToPage("/Account/Login", new { area = "Identity", returnUrl });
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
                    TempData["Toast"] = "Nao foi possivel criar utilizador local.";
                    return RedirectToPage("/Account/Login", new { area = "Identity", returnUrl });
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
                    TempData["Toast"] = "Nao foi possivel associar login Google.";
                    return RedirectToPage("/Account/Login", new { area = "Identity", returnUrl });
                }
            }

            await _signInManager.SignInAsync(user, isPersistent: false);

            if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
            {
                return LocalRedirect(returnUrl);
            }

            return RedirectToAction("Dashboard", "Home");
        }
    }
}
