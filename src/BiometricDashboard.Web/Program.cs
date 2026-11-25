using BiometricDashboard.Infrastructure;
using BiometricDashboard.Web.Authorization;
using BiometricDashboard.Web.Hubs;
using Microsoft.Extensions.DependencyInjection.Extensions;
using BiometricDashboard.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddRequestTimeouts();
builder.Services.AddSignalR();
builder.Services.AddHttpContextAccessor();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(8);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.Name = ".BiometricPortal.Session";
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.AccessDeniedPath = "/Account/AccessDenied";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.Cookie.Name = ".BiometricPortal.Auth";
        options.Cookie.HttpOnly = true;
    });

var permissionCodes = new[]
{
    "dashboard.view", "logs.view", "devices.view", "devices.manage", "device_users.view", "device_photos.view",
    "device_enroll_hub.view", "device_enroll_hub.save",
    "device_enroll_sync.view", "device_enroll_sync.run",
    "users.view", "users.manage", "roles.view", "roles.manage",
};

builder.Services.AddAuthorization(options =>
{
    foreach (var code in permissionCodes)
        options.AddPolicy(code, policy => policy.Requirements.Add(new PermissionRequirement(code)));

    options.AddPolicy("device_enroll_sync.page", policy => policy.RequireAssertion(ctx =>
        HasAnyPermission(ctx.User,
            "device_enroll_sync.view",
            "device_enroll_hub.view",
            "device_enroll_hub.save",
            "device_users.view",
            "devices.view")));

    options.AddPolicy("device_enroll_sync.run", policy => policy.RequireAssertion(ctx =>
        HasAnyPermission(ctx.User,
            "device_enroll_sync.run",
            "devices.manage",
            "device_enroll_hub.save")));
});

static bool HasAnyPermission(System.Security.Claims.ClaimsPrincipal user, params string[] codes) =>
    codes.Any(code => user.HasClaim("permission", code));
builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

builder.Services.AddSingleton<BiometricDashboard.Web.Services.DashboardLivePresence>();
builder.Services.AddSingleton<BiometricDashboard.Core.Interfaces.IDashboardLivePresence>(
    sp => sp.GetRequiredService<BiometricDashboard.Web.Services.DashboardLivePresence>());
builder.Services.AddBiometricInfrastructure(builder.Configuration);
builder.Services.Replace(ServiceDescriptor.Singleton<BiometricDashboard.Core.Interfaces.IEmployeeSyncNotifier, EmployeeSyncNotifier>());
builder.Services.AddScoped<BiometricDashboard.Web.Services.SelectedDeviceService>();
builder.Services.AddScoped<BiometricDashboard.Web.Services.EmployeePhotoBulkZipService>();
builder.Services.AddHostedService<LiveAttendanceNotifier>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseRequestTimeouts();
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<BiometricDashboard.Web.Middleware.InitialSetupMiddleware>();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Dashboard}/{action=Index}/{id?}");

app.MapHub<AttendanceHub>(AttendanceHub.HubPath);
app.MapHub<EmployeeSyncHub>(EmployeeSyncHub.HubPath);

app.Run();
