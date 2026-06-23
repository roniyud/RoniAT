using Microsoft.EntityFrameworkCore;
using RoniAT.TradingEngine.Contracts;
using RoniAT.TradingEngine.Data;
using RoniAT.TradingEngine.Models;
using RoniAT.TradingEngine.Services;
using RoniAT.TradingEngine.Validation;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
{
    options.AddPolicy("Dashboard", policy =>
    {
        policy
            .WithOrigins(
                "http://localhost:5173",
                "http://127.0.0.1:5173",
                "http://localhost:4173",
                "http://127.0.0.1:4173")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = null;
});

var connectionString = builder.Configuration.GetConnectionString("TradingDb")
    ?? "Data Source=storage/roniat.db";

var dbPath = GetSqlitePath(connectionString, builder.Environment.ContentRootPath);
if (!string.IsNullOrWhiteSpace(dbPath))
{
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
}

builder.Services.AddDbContext<TradingDbContext>(options =>
{
    options.UseSqlite(connectionString);
});
builder.Services.AddScoped<PaperBrokerAdapter>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
    await db.Database.EnsureCreatedAsync();
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("Dashboard");

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "trading-engine" }))
    .WithName("Health")
    .WithOpenApi();

app.MapPost("/api/signals", async (TradingSignalRequest request, TradingDbContext db, PaperBrokerAdapter paperBroker) =>
{
    var validation = TradingSignalValidator.Validate(request);
    if (!validation.Ok)
    {
        return Results.BadRequest(new ValidationErrorResponse(validation.Errors));
    }

    var normalized = validation.Signal!;
    var signal = TradingSignalRecord.FromRequest(normalized);

    db.Signals.Add(signal);
    db.AuditLogs.Add(AuditLogRecord.SignalAccepted(signal));
    await db.SaveChangesAsync();

    await paperBroker.ApplyEntrySignalAsync(signal, db);
    await db.SaveChangesAsync();

    return Results.Created($"/api/signals/{signal.Id}", TradingSignalResponse.FromRecord(signal));
})
.WithName("CreateSignal")
.WithOpenApi();

app.MapGet("/api/signals", async (TradingDbContext db) =>
{
    var signals = await db.Signals
        .OrderByDescending(signal => signal.Id)
        .Take(100)
        .Select(signal => TradingSignalResponse.FromRecord(signal))
        .ToListAsync();

    return Results.Ok(signals);
})
.WithName("GetSignals")
.WithOpenApi();

app.MapGet("/api/signals/{id:long}", async (long id, TradingDbContext db) =>
{
    var signal = await db.Signals.FindAsync(id);
    return signal is null
        ? Results.NotFound()
        : Results.Ok(TradingSignalResponse.FromRecord(signal));
})
.WithName("GetSignal")
.WithOpenApi();

app.MapGet("/api/orders", async (TradingDbContext db) =>
{
    var orders = await db.Orders
        .OrderByDescending(order => order.Id)
        .Take(100)
        .ToListAsync();

    return Results.Ok(orders);
})
.WithName("GetOrders")
.WithOpenApi();

app.MapGet("/api/executions", async (TradingDbContext db) =>
{
    var executions = await db.Executions
        .OrderByDescending(execution => execution.Id)
        .Take(100)
        .ToListAsync();

    return Results.Ok(executions);
})
.WithName("GetExecutions")
.WithOpenApi();

app.MapGet("/api/positions", async (TradingDbContext db) =>
{
    var positions = await db.Positions
        .OrderBy(position => position.Symbol)
        .ToListAsync();

    return Results.Ok(positions);
})
.WithName("GetPositions")
.WithOpenApi();

app.MapPost("/api/paper/orders/cancel-working", async (SymbolActionRequest request, TradingDbContext db, PaperBrokerAdapter paperBroker) =>
{
    var result = await paperBroker.CancelWorkingOrdersAsync(request.Symbol, db);
    await db.SaveChangesAsync();

    return Results.Ok(result);
})
.WithName("CancelWorkingPaperOrders")
.WithOpenApi();

app.MapPost("/api/paper/positions/close", async (SymbolActionRequest request, TradingDbContext db, PaperBrokerAdapter paperBroker) =>
{
    if (string.IsNullOrWhiteSpace(request.Symbol))
    {
        return Results.BadRequest(new ValidationErrorResponse(["symbol is required"]));
    }

    var result = await paperBroker.ClosePositionAsync(request.Symbol, db);
    await db.SaveChangesAsync();

    return Results.Ok(result);
})
.WithName("ClosePaperPosition")
.WithOpenApi();

app.MapPost("/api/paper/flatten", async (TradingDbContext db, PaperBrokerAdapter paperBroker) =>
{
    var result = await paperBroker.FlattenAsync(db);
    await db.SaveChangesAsync();

    return Results.Ok(result);
})
.WithName("FlattenPaperAccount")
.WithOpenApi();

app.Run();

static string? GetSqlitePath(string connectionString, string contentRootPath)
{
    const string prefix = "Data Source=";
    var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var dataSource = parts.FirstOrDefault(part => part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    if (dataSource is null) return null;

    var path = dataSource[prefix.Length..];
    return Path.IsPathRooted(path)
        ? path
        : Path.GetFullPath(path, contentRootPath);
}
