using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Serilog;
using OpenTelemetry.Trace;
using OpenTelemetry.Resources;
using MediatR;
using CarRental.Core;
using CarRental.Core.BackgroundJobs;
using CarRental.Core.Features;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration().ReadFrom.Configuration(builder.Configuration).CreateLogger();
builder.Host.UseSerilog();

builder.Services.AddOpenTelemetry().WithTracing(t => t
    .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("CarRental-API"))
    .AddAspNetCoreInstrumentation()
    .AddEntityFrameworkCoreInstrumentation());

builder.Services.AddDbContext<RentalDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddMediatR(cfg => cfg.RegisterServicesFromAssembly(typeof(RentalDbContext).Assembly));
builder.Services.AddHostedService<DistributedOutboxProcessor>();

builder.Services.AddRateLimiter(options => {
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("IpSafePolicy", httpContext => {
        var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(ip, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1)
        });
    });
});

var app = builder.Build();
app.UseSerilogRequestLogging();
app.UseRateLimiter();

// Database Validation Loop
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<RentalDbContext>();
    // Verify schema is initialized manually via script if DB is brand new
    try
    {
        await db.Database.CanConnectAsync();
    }
    catch
    {
        Log.Fatal("Database schema missing. Execute database_schema.sql manual script before booting.");
    }
}

app.MapGet("/api/cars/available", async (DateTime start, int days, IMediator m) =>
    Results.Ok(await m.Send(new GetAvailableCarTypesQuery(start, days))));

app.MapPost("/api/reservations", async (CreateReservationCommand cmd, IMediator m) => {
    try
    {
        var res = await m.Send(cmd);
        return res is not null ? Results.Ok(res) : Results.Conflict("No assets available or concurrency conflict intercepted.");
    }
    catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
}).RequireRateLimiting("IpSafePolicy");

app.Run();
