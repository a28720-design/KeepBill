using KeepBill.Data;
using KeepBill.Models;
using KeepBill.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using QuestPDF.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
QuestPDF.Settings.License = LicenseType.Community;

// Add services to the container.
var connectionString = ResolveConnectionString(builder.Configuration);
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
}

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(connectionString));
builder.Services.AddDatabaseDeveloperPageExceptionFilter();

builder.Services.AddDefaultIdentity<IdentityUser>(options => options.SignIn.RequireConfirmedAccount = true)
    .AddEntityFrameworkStores<ApplicationDbContext>();

var googleClientId = builder.Configuration["Authentication:Google:ClientId"];
var googleClientSecret = builder.Configuration["Authentication:Google:ClientSecret"];
if (!string.IsNullOrWhiteSpace(googleClientId) && !string.IsNullOrWhiteSpace(googleClientSecret))
{
    builder.Services
        .AddAuthentication()
        .AddGoogle(options =>
        {
            options.ClientId = googleClientId;
            options.ClientSecret = googleClientSecret;
        });
}

builder.Services.AddControllersWithViews();
builder.Services.Configure<SupabaseOptions>(builder.Configuration.GetSection("Supabase"));
builder.Services.Configure<EmailInboxOptions>(builder.Configuration.GetSection("EmailInbox"));
builder.Services.AddSingleton<IEmailSender, KeepBillEmailSender>();
builder.Services.AddScoped<IEmailInvoiceScannerService, MailKitEmailInvoiceScannerService>();
builder.Services.AddScoped<IUserEmailInboxSettingsService, UserEmailInboxSettingsService>();

var app = builder.Build();

// Ensure database schema is up to date in hosted environments (e.g., Render).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    db.Database.Migrate();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseMigrationsEndPoint();
}
else
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
app.MapRazorPages();

app.Run();

static string ResolveConnectionString(IConfiguration configuration)
{
    var raw = configuration.GetConnectionString("DefaultConnection");

    if (string.IsNullOrWhiteSpace(raw))
    {
        raw = configuration["ConnectionStrings__DefaultConnection"];
    }

    if (string.IsNullOrWhiteSpace(raw))
    {
        raw = configuration["DATABASE_URL"];
    }

    if (string.IsNullOrWhiteSpace(raw))
    {
        return string.Empty;
    }

    raw = raw.Trim().Trim('"');
    if (raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
        raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
    {
        return ConvertDatabaseUrlToNpgsql(raw);
    }

    return raw;
}

static string ConvertDatabaseUrlToNpgsql(string databaseUrl)
{
    var uri = new Uri(databaseUrl);
    var userInfoParts = uri.UserInfo.Split(':', 2);
    var username = userInfoParts.Length > 0 ? Uri.UnescapeDataString(userInfoParts[0]) : string.Empty;
    var password = userInfoParts.Length > 1 ? Uri.UnescapeDataString(userInfoParts[1]) : string.Empty;

    var builder = new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.IsDefaultPort ? 5432 : uri.Port,
        Username = username,
        Password = password,
        Database = uri.AbsolutePath.TrimStart('/'),
        SslMode = SslMode.Require
    };

    var query = uri.Query.TrimStart('?');
    if (!string.IsNullOrWhiteSpace(query))
    {
        foreach (var segment in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = segment.Split('=', 2);
            var key = Uri.UnescapeDataString(pair[0]).ToLowerInvariant();
            var value = pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : string.Empty;

            if (key == "sslmode" && Enum.TryParse<SslMode>(value, true, out var sslMode))
            {
                builder.SslMode = sslMode;
            }
        }
    }

    return builder.ConnectionString;
}
