using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using TwilioCaller.Api.Data;
using TwilioCaller.Api.Endpoints;
using TwilioCaller.Api.Realtime;
using TwilioCaller.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default")
                      ?? "Data Source=twilio-caller.db"));

builder.Services.AddHttpClient(TwilioClientFactory.HttpClientName);

builder.Services.AddSingleton<CredentialProtector>();
builder.Services.AddSingleton<SessionTokenService>();
builder.Services.AddSingleton<WebhookUrlBuilder>();
builder.Services.AddSingleton<TwilioClientFactory>();
builder.Services.AddSingleton<VoiceTokenService>();
builder.Services.AddScoped<TwilioApiService>();
builder.Services.AddScoped<ProvisioningService>();
builder.Services.AddScoped<ConnectionService>();
builder.Services.AddScoped<RealtimeNotifier>();

builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var sessions = new SessionTokenService(builder.Configuration);
        options.TokenValidationParameters = sessions.ValidationParameters;

        // SignalR's WebSocket transport cannot set an Authorization header, so the
        // token arrives as a query string parameter on the hub path only.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) &&
                    context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                {
                    context.Token = token;
                }

                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorization();

// The Flutter web build runs from a different origin than this API.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                     ?? Array.Empty<string>();
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
    {
        if (allowedOrigins.Contains("*"))
        {
            policy.SetIsOriginAllowed(_ => true);
        }
        else
        {
            policy.WithOrigins(allowedOrigins);
        }

        policy.AllowAnyHeader().AllowAnyMethod().AllowCredentials();
    }));

var app = builder.Build();

// Twilio always calls us over https; a PaaS TLS terminator would otherwise make the
// request look like http and break X-Twilio-Signature validation.
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    KnownNetworks = { },
    KnownProxies = { },
});

using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreated();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapApiEndpoints();
app.MapWebhookEndpoints();
app.MapHub<RealtimeHub>("/hubs/realtime");

app.Run();

/// <summary>Exposed so the test project can spin the app up in-process.</summary>
public partial class Program;
