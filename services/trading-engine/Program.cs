using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using RoniAT.TradingEngine.Contracts;
using RoniAT.TradingEngine.Data;
using RoniAT.TradingEngine.Hubs;
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
            .AllowAnyMethod()
            .AllowCredentials();
    });
});
builder.Services.AddSignalR();
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
builder.Services.AddScoped<IBrokerAdapter>(serviceProvider =>
{
    var mode = builder.Configuration.GetValue<string>("Broker:Mode") ?? "Paper";
    return mode.Equals("Paper", StringComparison.OrdinalIgnoreCase)
        ? serviceProvider.GetRequiredService<PaperBrokerAdapter>()
        : throw new InvalidOperationException($"Unsupported broker mode: {mode}");
});
builder.Services.AddSingleton<IMarketDataProvider, MockMarketDataProvider>();

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

app.MapHub<TradingHub>("/hubs/trading");

app.MapPost("/api/signals", async (TradingSignalRequest request, TradingDbContext db, IBrokerAdapter brokerAdapter, IHubContext<TradingHub> hub) =>
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

    await brokerAdapter.ApplyEntrySignalAsync(signal, db);
    await db.SaveChangesAsync();

    await BroadcastTradingUpdateAsync(hub, "signal.created", signal.Symbol);

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

app.MapGet("/api/market-data/candles", (string? symbol, string? timeframe, IMarketDataProvider marketDataProvider) =>
{
    try
    {
        var candles = marketDataProvider.GetCandles(symbol ?? "MNQ1!", timeframe ?? "5m");
        return Results.Ok(candles);
    }
    catch (ArgumentException error)
    {
        return Results.BadRequest(new ValidationErrorResponse([error.Message]));
    }
})
.WithName("GetCandles")
.WithOpenApi();

app.MapGet("/api/broker", (IBrokerAdapter brokerAdapter) => Results.Ok(new { mode = brokerAdapter.Name }))
.WithName("GetBrokerMode")
.WithOpenApi();

app.MapPost("/api/broker/orders/cancel-working", async (SymbolActionRequest request, TradingDbContext db, IBrokerAdapter brokerAdapter, IHubContext<TradingHub> hub) =>
{
    var result = await brokerAdapter.CancelWorkingOrdersAsync(request.Symbol, db);
    await db.SaveChangesAsync();

    await BroadcastTradingUpdateAsync(hub, "orders.updated", request.Symbol);

    return Results.Ok(result);
})
.WithName("CancelWorkingOrders")
.WithOpenApi();

app.MapPost("/api/broker/positions/close", async (SymbolActionRequest request, TradingDbContext db, IBrokerAdapter brokerAdapter, IHubContext<TradingHub> hub) =>
{
    if (string.IsNullOrWhiteSpace(request.Symbol))
    {
        return Results.BadRequest(new ValidationErrorResponse(["symbol is required"]));
    }

    var result = await brokerAdapter.ClosePositionAsync(request.Symbol, db);
    await db.SaveChangesAsync();

    await BroadcastTradingUpdateAsync(hub, "positions.updated", request.Symbol);

    return Results.Ok(result);
})
.WithName("ClosePosition")
.WithOpenApi();

app.MapPost("/api/broker/flatten", async (TradingDbContext db, IBrokerAdapter brokerAdapter, IHubContext<TradingHub> hub) =>
{
    var result = await brokerAdapter.FlattenAsync(db);
    await db.SaveChangesAsync();

    await BroadcastTradingUpdateAsync(hub, "account.flattened", null);

    return Results.Ok(result);
})
.WithName("FlattenAccount")
.WithOpenApi();

app.MapPost("/api/paper/orders/cancel-working", async (SymbolActionRequest request, TradingDbContext db, IBrokerAdapter brokerAdapter, IHubContext<TradingHub> hub) =>
{
    var result = await brokerAdapter.CancelWorkingOrdersAsync(request.Symbol, db);
    await db.SaveChangesAsync();

    await BroadcastTradingUpdateAsync(hub, "orders.updated", request.Symbol);

    return Results.Ok(result);
})
.WithName("CancelWorkingPaperOrders")
.WithOpenApi();

app.MapPost("/api/paper/positions/close", async (SymbolActionRequest request, TradingDbContext db, IBrokerAdapter brokerAdapter, IHubContext<TradingHub> hub) =>
{
    if (string.IsNullOrWhiteSpace(request.Symbol))
    {
        return Results.BadRequest(new ValidationErrorResponse(["symbol is required"]));
    }

    var result = await brokerAdapter.ClosePositionAsync(request.Symbol, db);
    await db.SaveChangesAsync();

    await BroadcastTradingUpdateAsync(hub, "positions.updated", request.Symbol);

    return Results.Ok(result);
})
.WithName("ClosePaperPosition")
.WithOpenApi();

app.MapPost("/api/paper/flatten", async (TradingDbContext db, IBrokerAdapter brokerAdapter, IHubContext<TradingHub> hub) =>
{
    var result = await brokerAdapter.FlattenAsync(db);
    await db.SaveChangesAsync();

    await BroadcastTradingUpdateAsync(hub, "account.flattened", null);

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

static Task BroadcastTradingUpdateAsync(IHubContext<TradingHub> hub, string eventType, string? symbol)
{
    return hub.Clients.All.SendAsync("trading.updated", new
    {
        event_type = eventType,
        symbol,
        occurred_at = DateTimeOffset.UtcNow
    });
}
