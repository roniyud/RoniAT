using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.FileProviders;
using Microsoft.Data.Sqlite;
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
        var configuredOrigins = builder.Configuration
            .GetSection("Dashboard:AllowedOrigins")
            .Get<string[]>() ?? [];
        var origins = new[]
            {
                "http://localhost:5173",
                "http://127.0.0.1:5173",
                "http://localhost:4173",
                "http://127.0.0.1:4173"
            }
            .Concat(configuredOrigins)
            .Where(origin => !string.IsNullOrWhiteSpace(origin))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        policy
            .WithOrigins(origins)
            .WithExposedHeaders("X-Market-Data-Source", "X-Market-Data-Warning")
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
builder.Services.Configure<RiskSettings>(builder.Configuration.GetSection("Risk"));
builder.Services.PostConfigure<RiskSettings>(settings =>
{
    if (settings.AllowedSymbols.Length == 0)
    {
        settings.AllowedSymbols = ["MNQ1!"];
    }
});
builder.Services.AddSingleton<RiskSettingsStore>();
builder.Services.AddSingleton<DashboardAuthService>();
builder.Services.AddSingleton<DailyPerformanceStore>();
builder.Services.AddScoped<RiskValidator>();
builder.Services.AddHttpClient("tastytrade", client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.Configure<BrokerSettings>(options =>
{
    options.Mode = builder.Configuration.GetValue<string>("Broker:Mode") ?? "Paper";

    var ibkrSection = builder.Configuration.GetSection("IBKR");
    options.IbkrPaper = new IBKRSettings
    {
        Host = ibkrSection.GetValue<string>("Paper:Host") ?? ibkrSection.GetValue<string>("Host") ?? "127.0.0.1",
        Port = ibkrSection.GetValue<int?>("Paper:Port") ?? ibkrSection.GetValue<int?>("Port") ?? 4002,
        ClientId = ibkrSection.GetValue<int?>("Paper:ClientId") ?? ibkrSection.GetValue<int?>("ClientId") ?? 5324,
        Account = ibkrSection.GetValue<string>("Paper:Account") ?? ibkrSection.GetValue<string>("Account") ?? "",
        Enabled = ibkrSection.GetValue<bool?>("Paper:Enabled") ?? ibkrSection.GetValue<bool?>("Enabled") ?? false,
        ReadOnly = ibkrSection.GetValue<bool?>("Paper:ReadOnly") ?? ibkrSection.GetValue<bool?>("ReadOnly") ?? true
    };
    options.IbkrLive = new IBKRSettings
    {
        Host = ibkrSection.GetValue<string>("Live:Host") ?? "127.0.0.1",
        Port = ibkrSection.GetValue<int?>("Live:Port") ?? 4001,
        ClientId = ibkrSection.GetValue<int?>("Live:ClientId") ?? 11,
        Account = ibkrSection.GetValue<string>("Live:Account") ?? "",
        Enabled = ibkrSection.GetValue<bool?>("Live:Enabled") ?? false,
        ReadOnly = ibkrSection.GetValue<bool?>("Live:ReadOnly") ?? true
    };

    var tastytradeSection = builder.Configuration.GetSection("Tastytrade");
    options.TastytradeSandbox = new TastytradeSettings
    {
        ApiBaseUrl = tastytradeSection.GetValue<string>("Sandbox:ApiBaseUrl") ?? "https://api.cert.tastyworks.com",
        StreamerBaseUrl = tastytradeSection.GetValue<string>("Sandbox:StreamerBaseUrl") ?? "wss://streamer.cert.tastyworks.com",
        AuthorizationUrl = tastytradeSection.GetValue<string>("Sandbox:AuthorizationUrl") ?? "https://api.cert.tastyworks.com/oauth/authorize",
        TokenUrl = tastytradeSection.GetValue<string>("Sandbox:TokenUrl") ?? "https://api.cert.tastyworks.com/oauth/token",
        ClientId = tastytradeSection.GetValue<string>("Sandbox:ClientId") ?? "",
        ClientSecret = tastytradeSection.GetValue<string>("Sandbox:ClientSecret") ?? "",
        RedirectUri = tastytradeSection.GetValue<string>("Sandbox:RedirectUri") ?? "http://localhost:3001/api/tastytrade/oauth/callback",
        Username = tastytradeSection.GetValue<string>("Sandbox:Username") ?? "",
        Password = tastytradeSection.GetValue<string>("Sandbox:Password") ?? "",
        AccessToken = tastytradeSection.GetValue<string>("Sandbox:AccessToken") ?? "",
        RefreshToken = tastytradeSection.GetValue<string>("Sandbox:RefreshToken") ?? "",
        AccessTokenExpiresAt = tastytradeSection.GetValue<DateTimeOffset?>("Sandbox:AccessTokenExpiresAt"),
        AccountNumber = tastytradeSection.GetValue<string>("Sandbox:AccountNumber") ?? "",
        Enabled = tastytradeSection.GetValue<bool?>("Sandbox:Enabled") ?? false,
        ReadOnly = tastytradeSection.GetValue<bool?>("Sandbox:ReadOnly") ?? true
    };
    options.TastytradeLive = new TastytradeSettings
    {
        ApiBaseUrl = tastytradeSection.GetValue<string>("Live:ApiBaseUrl") ?? "https://api.tastyworks.com",
        StreamerBaseUrl = tastytradeSection.GetValue<string>("Live:StreamerBaseUrl") ?? "wss://streamer.tastyworks.com",
        AuthorizationUrl = tastytradeSection.GetValue<string>("Live:AuthorizationUrl") ?? "https://api.tastyworks.com/oauth/authorize",
        TokenUrl = tastytradeSection.GetValue<string>("Live:TokenUrl") ?? "https://api.tastyworks.com/oauth/token",
        ClientId = tastytradeSection.GetValue<string>("Live:ClientId") ?? "",
        ClientSecret = tastytradeSection.GetValue<string>("Live:ClientSecret") ?? "",
        RedirectUri = tastytradeSection.GetValue<string>("Live:RedirectUri") ?? "http://localhost:3001/api/tastytrade/oauth/callback",
        Username = tastytradeSection.GetValue<string>("Live:Username") ?? "",
        Password = tastytradeSection.GetValue<string>("Live:Password") ?? "",
        AccessToken = tastytradeSection.GetValue<string>("Live:AccessToken") ?? "",
        RefreshToken = tastytradeSection.GetValue<string>("Live:RefreshToken") ?? "",
        AccessTokenExpiresAt = tastytradeSection.GetValue<DateTimeOffset?>("Live:AccessTokenExpiresAt"),
        AccountNumber = tastytradeSection.GetValue<string>("Live:AccountNumber") ?? "",
        Enabled = tastytradeSection.GetValue<bool?>("Live:Enabled") ?? false,
        ReadOnly = tastytradeSection.GetValue<bool?>("Live:ReadOnly") ?? true
    };
});
builder.Services.AddSingleton<BrokerSettingsStore>();
builder.Services.AddSingleton<BrokerConnectionStateStore>();
builder.Services.AddSingleton<SystemOwnedPositionTracker>();
builder.Services.AddSingleton<IBKRConnectionSession>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<IBKRConnectionSession>());
builder.Services.AddHostedService<StopLossFailsafeService>();
builder.Services.AddHostedService<UnmanagedPositionGuardService>();
builder.Services.AddHostedService<SystemManagedProtectionService>();
builder.Services.AddHostedService<TastytradeTokenRefreshService>();
builder.Services.AddScoped<IBKRConnectionTester>();
builder.Services.AddScoped<TastytradeConnectionTester>();
builder.Services.AddSingleton<TastytradeOAuthStateStore>();
builder.Services.AddSingleton<TastytradeOAuthService>();
builder.Services.AddScoped<TastytradeAccountClient>();
builder.Services.AddScoped<TastytradeOrderClient>();
builder.Services.AddSingleton<TastytradeQuoteTokenClient>();
builder.Services.AddSingleton<TastytradeInstrumentClient>();
builder.Services.AddSingleton<TastytradeRealtimeMarketDataService>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<TastytradeRealtimeMarketDataService>());
builder.Services.AddSingleton<TastytradePendingProtectionService>();
builder.Services.AddHostedService(serviceProvider => serviceProvider.GetRequiredService<TastytradePendingProtectionService>());
builder.Services.AddScoped<PaperBrokerAdapter>();
builder.Services.AddScoped<IBKRBrokerAdapter>();
builder.Services.AddScoped<TastytradeBrokerAdapter>();
builder.Services.AddScoped<BrokerRouterAdapter>();
builder.Services.AddScoped<IBrokerAdapter>(serviceProvider => serviceProvider.GetRequiredService<BrokerRouterAdapter>());
builder.Services.AddSingleton<MockMarketDataProvider>();
builder.Services.AddSingleton<IBKRMarketDataProvider>();
builder.Services.AddSingleton<TastytradeMarketDataProvider>();
builder.Services.AddSingleton<MarketDataRouterProvider>();
builder.Services.AddSingleton<IMarketDataProvider>(serviceProvider => serviceProvider.GetRequiredService<MarketDataRouterProvider>());

var app = builder.Build();
var dashboardDistPath = Path.GetFullPath(Path.Combine(
    app.Environment.ContentRootPath,
    "..",
    "..",
    "apps",
    "trading-dashboard",
    "dist"));
var dashboardIndexPath = Path.Combine(dashboardDistPath, "index.html");
var hasBuiltDashboard = File.Exists(dashboardIndexPath);

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
    await db.Database.EnsureCreatedAsync();
    await EnsureClosedPositionsTableAsync(db);
    await EnsurePositionOwnershipColumnAsync(db);
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (hasBuiltDashboard)
{
    var dashboardFileProvider = new PhysicalFileProvider(dashboardDistPath);
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = dashboardFileProvider
    });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = dashboardFileProvider
    });
}

app.UseCors("Dashboard");

app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var requiresAuth = path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs/trading");
    var isAuthEndpoint = path.StartsWithSegments("/api/auth");
    var isTastytradeOAuthCallback = path.StartsWithSegments("/api/tastytrade/oauth/callback");

    if (!requiresAuth || isAuthEndpoint || isTastytradeOAuthCallback)
    {
        await next();
        return;
    }

    var authService = context.RequestServices.GetRequiredService<DashboardAuthService>();
    var token = ReadBearerToken(context);
    if (!authService.IsValidToken(token))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { message = "Unauthorized" });
        return;
    }

    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "trading-engine" }))
    .WithName("Health")
    .WithOpenApi();

app.MapPost("/api/auth/login", (LoginRequest request, DashboardAuthService authService) =>
{
    var result = authService.Login(request.Username, request.Password);
    return result.Ok
        ? Results.Ok(new LoginResponse(true, result.Token, result.ExpiresAt, result.Message))
        : Results.Unauthorized();
})
.WithName("Login")
.WithOpenApi();

app.MapPost("/api/auth/logout", (HttpContext context, DashboardAuthService authService) =>
{
    authService.Logout(ReadBearerToken(context));
    return Results.Ok(new { ok = true });
})
.WithName("Logout")
.WithOpenApi();

app.MapHub<TradingHub>("/hubs/trading");

app.MapPost("/api/signals", async (TradingSignalRequest request, TradingDbContext db, IBrokerAdapter brokerAdapter, RiskValidator riskValidator, RiskSettingsStore riskSettingsStore, IMarketDataProvider marketDataProvider, IHubContext<TradingHub> hub) =>
    await ProcessSignalAsync(request, isManualTrade: false, db, brokerAdapter, riskValidator, riskSettingsStore, marketDataProvider, hub))
.WithName("CreateSignal")
.WithOpenApi();

app.MapPost("/api/manual-trades", async (TradingSignalRequest request, TradingDbContext db, IBrokerAdapter brokerAdapter, RiskValidator riskValidator, RiskSettingsStore riskSettingsStore, IMarketDataProvider marketDataProvider, IHubContext<TradingHub> hub) =>
    await ProcessSignalAsync(request, isManualTrade: true, db, brokerAdapter, riskValidator, riskSettingsStore, marketDataProvider, hub))
.WithName("CreateManualTrade")
.WithOpenApi();

app.MapPost("/api/market-orders", async (MarketOrderRequest request, TradingDbContext db, IBrokerAdapter brokerAdapter, RiskSettingsStore riskSettingsStore, DailyPerformanceStore dailyPerformanceStore, IHubContext<TradingHub> hub) =>
    await ProcessMarketOrderAsync(request, db, brokerAdapter, riskSettingsStore, dailyPerformanceStore, hub))
.WithName("CreateMarketOrder")
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

app.MapGet("/api/orders", async (TradingDbContext db, BrokerSettingsStore brokerSettingsStore, TastytradeAccountClient tastytradeClient, CancellationToken cancellationToken) =>
{
    if (brokerSettingsStore.Get().Mode.Equals("Tastytrade", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            return Results.Ok(await tastytradeClient.GetLiveOrdersAsync(cancellationToken));
        }
        catch (InvalidOperationException error)
        {
            return Results.BadRequest(new ValidationErrorResponse([error.Message]));
        }
    }

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

app.MapGet("/api/positions", async (TradingDbContext db, BrokerSettingsStore brokerSettingsStore, TastytradeAccountClient tastytradeClient, CancellationToken cancellationToken) =>
{
    if (brokerSettingsStore.Get().Mode.Equals("Tastytrade", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            var managedKeys = await GetManagedPositionKeysAsync(db, cancellationToken);
            var tastytradePositions = await tastytradeClient.GetPositionsAsync(cancellationToken);
            return Results.Ok(tastytradePositions
                .Select(position => PositionResponse.FromRecord(position, managedKeys.Contains(ToOwnershipKey(position.Symbol))))
                .ToArray());
        }
        catch (InvalidOperationException error)
        {
            return Results.BadRequest(new ValidationErrorResponse([error.Message]));
        }
    }

    var positions = await db.Positions
        .OrderBy(position => position.Symbol)
        .ToListAsync();

    return Results.Ok(positions.Select(position => PositionResponse.FromRecord(position)).ToArray());
})
.WithName("GetPositions")
.WithOpenApi();

app.MapGet("/api/positions/closed", async (string? date, TradingDbContext db) =>
{
    var filterDate = ParseDateOnly(date) ?? DateOnly.FromDateTime(DateTime.Now);

    var closedPositions = (await db.ClosedPositions.ToListAsync())
        .Where(position => DateOnly.FromDateTime(position.ClosedAt.LocalDateTime) == filterDate)
        .OrderByDescending(position => position.ClosedAt)
        .Take(500)
        .ToList();

    return Results.Ok(closedPositions);
})
.WithName("GetClosedPositions")
.WithOpenApi();

app.MapGet("/api/audit-logs", async (TradingDbContext db) =>
{
    var auditLogs = await db.AuditLogs
        .OrderByDescending(audit => audit.Id)
        .Take(200)
        .Select(audit => AuditLogResponse.FromRecord(audit))
        .ToListAsync();

    return Results.Ok(auditLogs);
})
.WithName("GetAuditLogs")
.WithOpenApi();

app.MapGet("/api/performance/daily", async (TradingDbContext db) =>
{
    var snapshot = await GetDailyPerformanceAsync(db, DateOnly.FromDateTime(DateTime.Now));
    return Results.Ok(new
    {
        date = snapshot.Date.ToString("yyyy-MM-dd"),
        realized_pnl = snapshot.RealizedPnl,
        closed_trades = snapshot.ClosedTrades
    });
})
.WithName("GetDailyPerformance")
.WithOpenApi();

app.MapGet("/api/market-data/candles", async (
    string? symbol,
    string? timeframe,
    HttpContext httpContext,
    TradingDbContext db,
    IMarketDataProvider marketDataProvider,
    MarketDataRouterProvider marketDataRouterProvider,
    MockMarketDataProvider fallbackProvider,
    CancellationToken cancellationToken) =>
{
    var normalizedSymbol = string.IsNullOrWhiteSpace(symbol) ? "MNQ1!" : symbol.Trim().ToUpperInvariant();
    var normalizedTimeframe = string.IsNullOrWhiteSpace(timeframe) ? "5m" : timeframe.Trim().ToLowerInvariant();

    try
    {
        using var marketDataTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        marketDataTimeout.CancelAfter(TimeSpan.FromSeconds(8));
        var candles = await marketDataProvider.GetCandlesAsync(normalizedSymbol, normalizedTimeframe, marketDataTimeout.Token);
        httpContext.Response.Headers["X-Market-Data-Source"] = marketDataRouterProvider.ActiveSource;
        return Results.Ok(candles);
    }
    catch (ArgumentException error)
    {
        return Results.BadRequest(new ValidationErrorResponse([error.Message]));
    }
    catch (Exception error) when (error is InvalidOperationException or OperationCanceledException)
    {
        var anchorPrice = await db.Positions
            .Where(position => position.Symbol == normalizedSymbol)
            .Select(position => (decimal?)position.AveragePrice)
            .SingleOrDefaultAsync(CancellationToken.None);

        var fallbackCandles = fallbackProvider.GetCandles(normalizedSymbol, normalizedTimeframe, anchorPrice);
        httpContext.Response.Headers["X-Market-Data-Source"] = "fallback";
        httpContext.Response.Headers["X-Market-Data-Warning"] = error is OperationCanceledException
            ? $"{marketDataRouterProvider.ActiveSource} market data timed out; showing fallback candles"
            : error.Message;

        return Results.Ok(fallbackCandles);
    }
})
.WithName("GetCandles")
.WithOpenApi();

app.MapPost("/api/market-data/stream", async (
    MarketDataStreamRequest request,
    BrokerSettingsStore brokerSettingsStore,
    TastytradeRealtimeMarketDataService tastytradeRealtimeMarketDataService,
    IBKRConnectionSession connectionSession,
    CancellationToken cancellationToken) =>
{
    var normalizedSymbol = string.IsNullOrWhiteSpace(request.Symbol)
        ? "MNQ1!"
        : request.Symbol.Trim().ToUpperInvariant();

    var brokerSettings = brokerSettingsStore.Get();
    if (brokerSettings.Mode.Equals("IBKR", StringComparison.OrdinalIgnoreCase))
    {
        await connectionSession.EnsureStreamingMarketDataAsync(normalizedSymbol, cancellationToken);
    }
    else if (brokerSettings.Mode.Equals("Tastytrade", StringComparison.OrdinalIgnoreCase))
    {
        await tastytradeRealtimeMarketDataService.EnsureStreamingMarketDataAsync(normalizedSymbol, cancellationToken);
    }

    return Results.Ok(new
    {
        ok = true,
        symbol = normalizedSymbol,
        source = brokerSettings.Mode.Equals("Tastytrade", StringComparison.OrdinalIgnoreCase)
            ? "tastytrade"
            : brokerSettings.Mode.Equals("IBKR", StringComparison.OrdinalIgnoreCase) ? "ibkr" : "fallback"
    });
})
.WithName("StartMarketDataStream")
.WithOpenApi();

app.MapGet("/api/broker", (IBrokerAdapter brokerAdapter) => Results.Ok(BrokerStatusResponse.FromStatus(brokerAdapter.GetStatus())))
.WithName("GetBrokerMode")
.WithOpenApi();

app.MapGet("/api/broker/settings", (BrokerSettingsStore settingsStore) =>
{
    return Results.Ok(BrokerSettingsResponse.FromSettings(settingsStore.Get()));
})
.WithName("GetBrokerSettings")
.WithOpenApi();

app.MapPut("/api/broker/settings", async (BrokerSettingsUpdateRequest request, BrokerSettingsStore settingsStore, TradingDbContext db, IHubContext<TradingHub> hub) =>
{
    var result = settingsStore.Update(new BrokerSettings
    {
        Mode = request.Mode,
        IbkrEnvironment = request.IbkrEnvironment,
        TastytradeEnvironment = request.TastytradeEnvironment,
        IbkrPaper = new IBKRSettings
        {
            Host = request.IbkrPaper.Host,
            Port = request.IbkrPaper.Port,
            ClientId = request.IbkrPaper.ClientId,
            Account = request.IbkrPaper.Account,
            Enabled = request.IbkrPaper.Enabled,
            ReadOnly = request.IbkrPaper.ReadOnly
        },
        IbkrLive = new IBKRSettings
        {
            Host = request.IbkrLive.Host,
            Port = request.IbkrLive.Port,
            ClientId = request.IbkrLive.ClientId,
            Account = request.IbkrLive.Account,
            Enabled = request.IbkrLive.Enabled,
            ReadOnly = request.IbkrLive.ReadOnly
        },
        TastytradeSandbox = new TastytradeSettings
        {
            ApiBaseUrl = request.TastytradeSandbox.ApiBaseUrl,
            StreamerBaseUrl = request.TastytradeSandbox.StreamerBaseUrl,
            AuthorizationUrl = request.TastytradeSandbox.AuthorizationUrl,
            TokenUrl = request.TastytradeSandbox.TokenUrl,
            ClientId = request.TastytradeSandbox.ClientId,
            ClientSecret = request.TastytradeSandbox.ClientSecret,
            RedirectUri = request.TastytradeSandbox.RedirectUri,
            Username = request.TastytradeSandbox.Username,
            Password = request.TastytradeSandbox.Password,
            AccessToken = request.TastytradeSandbox.AccessToken,
            RefreshToken = request.TastytradeSandbox.RefreshToken,
            AccessTokenExpiresAt = request.TastytradeSandbox.AccessTokenExpiresAt,
            AccountNumber = request.TastytradeSandbox.AccountNumber,
            Enabled = request.TastytradeSandbox.Enabled,
            ReadOnly = request.TastytradeSandbox.ReadOnly
        },
        TastytradeLive = new TastytradeSettings
        {
            ApiBaseUrl = request.TastytradeLive.ApiBaseUrl,
            StreamerBaseUrl = request.TastytradeLive.StreamerBaseUrl,
            AuthorizationUrl = request.TastytradeLive.AuthorizationUrl,
            TokenUrl = request.TastytradeLive.TokenUrl,
            ClientId = request.TastytradeLive.ClientId,
            ClientSecret = request.TastytradeLive.ClientSecret,
            RedirectUri = request.TastytradeLive.RedirectUri,
            Username = request.TastytradeLive.Username,
            Password = request.TastytradeLive.Password,
            AccessToken = request.TastytradeLive.AccessToken,
            RefreshToken = request.TastytradeLive.RefreshToken,
            AccessTokenExpiresAt = request.TastytradeLive.AccessTokenExpiresAt,
            AccountNumber = request.TastytradeLive.AccountNumber,
            Enabled = request.TastytradeLive.Enabled,
            ReadOnly = request.TastytradeLive.ReadOnly
        }
    });

    if (!result.Ok)
    {
        return Results.BadRequest(new ValidationErrorResponse(result.Errors));
    }

    db.AuditLogs.Add(AuditLogRecord.BrokerAction(
        "broker.settings_updated",
        $"Broker settings updated: mode={result.Settings.Mode}, ibkrEnvironment={result.Settings.IbkrEnvironment}"));
    await db.SaveChangesAsync();

    await BroadcastTradingUpdateAsync(hub, "broker.settings_updated", null);

    return Results.Ok(BrokerSettingsResponse.FromSettings(result.Settings));
})
.WithName("UpdateBrokerSettings")
.WithOpenApi();

app.MapPost("/api/broker/test-connection", async (BrokerSettingsStore settingsStore, IBKRConnectionTester ibkrTester, TastytradeConnectionTester tastytradeTester, TradingDbContext db, IHubContext<TradingHub> hub, CancellationToken cancellationToken) =>
{
    var settings = settingsStore.Get();
    var result = settings.Mode.Equals("Tastytrade", StringComparison.OrdinalIgnoreCase)
        ? await tastytradeTester.TestAsync(cancellationToken)
        : await ibkrTester.TestAsync(cancellationToken);
    foreach (var audit in BuildBrokerConnectionAudit(result))
    {
        db.AuditLogs.Add(audit);
    }

    await db.SaveChangesAsync(cancellationToken);

    await BroadcastTradingUpdateAsync(hub, result.Ok ? "broker.connection_test_succeeded" : "broker.connection_test_failed", null);

    return Results.Ok(BrokerConnectionTestResponse.FromResult(result));
})
.WithName("TestBrokerConnection")
.WithOpenApi();

app.MapPost("/api/tastytrade/oauth/start", (TastytradeOAuthService oauthService) =>
{
    try
    {
        var result = oauthService.BuildAuthorizationUrl();
        return Results.Ok(new
        {
            authorization_url = result.AuthorizationUrl,
            environment = result.Environment,
            redirect_uri = result.RedirectUri
        });
    }
    catch (InvalidOperationException error)
    {
        return Results.BadRequest(new ValidationErrorResponse([error.Message]));
    }
})
.WithName("StartTastytradeOAuth")
.WithOpenApi();

app.MapPost("/api/tastytrade/oauth/refresh", async (TastytradeOAuthService oauthService, TradingDbContext db, IHubContext<TradingHub> hub, CancellationToken cancellationToken) =>
{
    var result = await oauthService.RefreshAccessTokenAsync(cancellationToken);
    db.AuditLogs.Add(AuditLogRecord.BrokerAction(
        result.Ok ? "tastytrade.token_refreshed" : "tastytrade.token_refresh_failed",
        result.Message));
    await db.SaveChangesAsync(cancellationToken);

    if (result.Ok)
    {
        await BroadcastTradingUpdateAsync(hub, "broker.settings_updated", null);
    }

    return result.Ok
        ? Results.Ok(new
        {
            ok = true,
            message = result.Message,
            environment = result.Environment,
            access_token_expires_at = result.AccessTokenExpiresAt
        })
        : Results.BadRequest(new ValidationErrorResponse([result.Message]));
})
.WithName("RefreshTastytradeOAuthToken")
.WithOpenApi();

app.MapGet("/api/tastytrade/oauth/callback", async (string? code, string? state, string? error, TastytradeOAuthService oauthService, TradingDbContext db, IHubContext<TradingHub> hub, CancellationToken cancellationToken) =>
{
    var result = await oauthService.HandleCallbackAsync(code, state, error, cancellationToken);
    db.AuditLogs.Add(AuditLogRecord.BrokerAction(
        result.Ok ? "tastytrade.oauth_connected" : "tastytrade.oauth_failed",
        result.Message));
    await db.SaveChangesAsync(cancellationToken);

    if (result.Ok)
    {
        await BroadcastTradingUpdateAsync(hub, "broker.settings_updated", null);
    }

    var title = result.Ok ? "Tastytrade connected" : "Tastytrade connection failed";
    var color = result.Ok ? "#13795b" : "#b42318";
    var html = $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>{{title}}</title>
          <style>
            body { font-family: Arial, sans-serif; margin: 40px; color: #111827; }
            .box { max-width: 720px; border: 1px solid #d7dee8; border-radius: 8px; padding: 24px; }
            h1 { color: {{color}}; font-size: 22px; margin: 0 0 12px; }
            p { line-height: 1.5; }
            code { background: #f3f4f6; padding: 2px 5px; border-radius: 4px; }
          </style>
        </head>
        <body>
          <div class="box">
            <h1>{{title}}</h1>
            <p>{{System.Net.WebUtility.HtmlEncode(result.Message)}}</p>
            <p>You can close this window and return to RoniAT.</p>
          </div>
        </body>
        </html>
        """;

    return Results.Content(html, "text/html; charset=utf-8");
})
.WithName("TastytradeOAuthCallback")
.WithOpenApi();

app.MapGet("/api/risk/settings", (RiskSettingsStore settingsStore) =>
{
    return Results.Ok(RiskSettingsResponse.FromSettings(settingsStore.Get()));
})
.WithName("GetRiskSettings")
.WithOpenApi();

app.MapPut("/api/risk/settings", async (RiskSettingsUpdateRequest request, RiskSettingsStore settingsStore, TradingDbContext db, IHubContext<TradingHub> hub) =>
{
    var result = settingsStore.Update(new RiskSettings
    {
        MaxContractsPerSignal = request.MaxContractsPerSignal,
        MaxLossPerTrade = request.MaxLossPerTrade,
        MaxDailyLoss = request.MaxDailyLoss,
        MaxEntryPriceDeviationPoints = request.MaxEntryPriceDeviationPoints,
        ChartMarketProtectionDistancePoints = request.ChartMarketProtectionDistancePoints,
        AllowedSymbols = request.AllowedSymbols.ToArray(),
        TestMode = request.TestMode,
        IgnoreTakeProfit2 = request.IgnoreTakeProfit2,
        EnableAutoTrading = request.EnableAutoTrading,
        RejectDuplicateSignals = request.RejectDuplicateSignals,
        DuplicateWindowSeconds = request.DuplicateWindowSeconds,
        AllowPositionStacking = request.AllowPositionStacking,
        TradingLocked = request.TradingLocked,
        EmergencyStopActive = request.EmergencyStopActive,
        CloseUnmanagedBrokerPositions = request.CloseUnmanagedBrokerPositions,
        SystemManagedProtectionEnabled = request.SystemManagedProtectionEnabled,
        StopLossFailsafeEnabled = request.StopLossFailsafeEnabled,
        StopLossFailsafePollSeconds = request.StopLossFailsafePollSeconds,
        StopLossFailsafeConfirmSeconds = request.StopLossFailsafeConfirmSeconds,
        StopLossFailsafeCooldownSeconds = request.StopLossFailsafeCooldownSeconds
    });

    if (!result.Ok)
    {
        return Results.BadRequest(new ValidationErrorResponse(result.Errors));
    }

    db.AuditLogs.Add(AuditLogRecord.RiskSettingsUpdated(result.Settings));
    await db.SaveChangesAsync();

    await BroadcastTradingUpdateAsync(hub, "risk.settings_updated", null);

    return Results.Ok(RiskSettingsResponse.FromSettings(result.Settings));
})
.WithName("UpdateRiskSettings")
.WithOpenApi();

app.MapPost("/api/safety/lock", async (RiskSettingsStore settingsStore, TradingDbContext db, IHubContext<TradingHub> hub) =>
{
    var current = settingsStore.Get();
    current.EnableAutoTrading = false;
    current.TradingLocked = true;

    var result = settingsStore.Update(current);
    db.AuditLogs.Add(AuditLogRecord.SafetyAction("safety.trading_locked", result.Settings));
    await db.SaveChangesAsync();
    await BroadcastTradingUpdateAsync(hub, "safety.trading_locked", null);

    return Results.Ok(RiskSettingsResponse.FromSettings(result.Settings));
})
.WithName("LockTrading")
.WithOpenApi();

app.MapPost("/api/safety/emergency-stop", async (RiskSettingsStore settingsStore, TradingDbContext db, IBrokerAdapter brokerAdapter, IHubContext<TradingHub> hub) =>
{
    var brokerResult = await brokerAdapter.FlattenAsync(db);

    var current = settingsStore.Get();
    current.EnableAutoTrading = false;
    current.TradingLocked = true;
    current.EmergencyStopActive = true;

    var result = settingsStore.Update(current);
    db.AuditLogs.Add(AuditLogRecord.SafetyAction("safety.emergency_stop", result.Settings, brokerResult));
    await db.SaveChangesAsync();
    await BroadcastTradingUpdateAsync(hub, "safety.emergency_stop", null);

    return Results.Ok(new
    {
        settings = RiskSettingsResponse.FromSettings(result.Settings),
        broker_result = brokerResult
    });
})
.WithName("EmergencyStop")
.WithOpenApi();

app.MapPost("/api/safety/resume", async (RiskSettingsStore settingsStore, TradingDbContext db, IHubContext<TradingHub> hub) =>
{
    var current = settingsStore.Get();
    current.EnableAutoTrading = true;
    current.TradingLocked = false;
    current.EmergencyStopActive = false;

    var result = settingsStore.Update(current);
    db.AuditLogs.Add(AuditLogRecord.SafetyAction("safety.trading_resumed", result.Settings));
    await db.SaveChangesAsync();
    await BroadcastTradingUpdateAsync(hub, "safety.trading_resumed", null);

    return Results.Ok(RiskSettingsResponse.FromSettings(result.Settings));
})
.WithName("ResumeTrading")
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

app.MapPut("/api/broker/protection", async (ProtectionUpdateRequest request, TradingDbContext db, IBrokerAdapter brokerAdapter, IHubContext<TradingHub> hub) =>
{
    if (string.IsNullOrWhiteSpace(request.Symbol))
    {
        return Results.BadRequest(new ValidationErrorResponse(["symbol is required"]));
    }

    if (request.StopLoss is null && request.TakeProfit is null)
    {
        return Results.BadRequest(new ValidationErrorResponse(["stop_loss or take_profit is required"]));
    }

    var result = await brokerAdapter.UpdateProtectionAsync(request.Symbol, request.StopLoss, request.TakeProfit, db);
    await db.SaveChangesAsync();
    await BroadcastTradingUpdateAsync(hub, "protection.updated", request.Symbol);

    return Results.Ok(result);
})
.WithName("UpdateProtection")
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

if (hasBuiltDashboard)
{
    app.MapFallback(async context =>
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs") || path.StartsWithSegments("/health"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.SendFileAsync(dashboardIndexPath);
    });
}

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

static IReadOnlyList<AuditLogRecord> BuildBrokerConnectionAudit(BrokerConnectionTestResult result)
{
    if (!result.Mode.Equals("IBKR", StringComparison.OrdinalIgnoreCase))
    {
        if (result.Mode.Equals("Tastytrade", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                AuditLogRecord.BrokerAction(
                    result.HandshakeOk ? "tastytrade.token_verified" : "tastytrade.token_failed",
                    $"Tastytrade {result.Environment} API test to {result.Host} - {result.Message}"),
                AuditLogRecord.BrokerAction(
                    result.Ok ? "broker.connection_test_succeeded" : "broker.connection_test_failed",
                    $"{result.Mode} {result.Environment} connection test to {result.Host} - {result.Message}")
            ];
        }

        return
        [
            AuditLogRecord.BrokerAction(
                result.Ok ? "broker.connection_test_succeeded" : "broker.connection_test_failed",
                $"{result.Mode} {result.Environment} connection test - {result.Message}")
        ];
    }

    var logs = new List<AuditLogRecord>
    {
        AuditLogRecord.BrokerAction(
            result.HandshakeOk ? "ibkr.handshake_succeeded" : "ibkr.handshake_failed",
            $"IBKR {result.Environment} API handshake to {result.Host}:{result.Port} - {result.Message}")
    };

    if (result.HandshakeOk && !string.IsNullOrWhiteSpace(result.SelectedAccount))
    {
        logs.Add(AuditLogRecord.BrokerAction(
            result.AccountVerified ? "ibkr.account_verified" : "ibkr.account_missing",
            result.AccountVerified
                ? $"IBKR {result.Environment} account {result.SelectedAccount} verified"
                : $"IBKR {result.Environment} account {result.SelectedAccount} was not found"));
    }

    logs.Add(AuditLogRecord.BrokerAction(
        result.Ok ? "broker.connection_test_succeeded" : "broker.connection_test_failed",
        $"{result.Mode} {result.Environment} connection test to {result.Host}:{result.Port} - {result.Message}"));

    return logs;
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

static string? ReadBearerToken(HttpContext context)
{
    var authorization = context.Request.Headers.Authorization.ToString();
    if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        return authorization["Bearer ".Length..].Trim();
    }

    return context.Request.Query.TryGetValue("access_token", out var queryToken)
        ? queryToken.ToString()
        : null;
}

static async Task EnsureClosedPositionsTableAsync(TradingDbContext db)
{
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "closed_positions" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_closed_positions" PRIMARY KEY AUTOINCREMENT,
            "Symbol" TEXT NOT NULL,
            "Direction" TEXT NOT NULL,
            "Quantity" INTEGER NOT NULL,
            "AveragePrice" TEXT NOT NULL,
            "ExitPrice" TEXT NULL,
            "StopLoss" TEXT NULL,
            "TakeProfit1" TEXT NULL,
            "TakeProfit2" TEXT NULL,
            "RealizedPnl" TEXT NULL,
            "CloseReason" TEXT NOT NULL,
            "OpenedAt" TEXT NOT NULL,
            "ClosedAt" TEXT NOT NULL
        );
        """);

    await db.Database.ExecuteSqlRawAsync("""
        CREATE INDEX IF NOT EXISTS "IX_closed_positions_ClosedAt"
        ON "closed_positions" ("ClosedAt");
        """);

    await db.Database.ExecuteSqlRawAsync("""
        CREATE INDEX IF NOT EXISTS "IX_closed_positions_Symbol"
        ON "closed_positions" ("Symbol");
        """);
}

static async Task EnsurePositionOwnershipColumnAsync(TradingDbContext db)
{
    if (!await SqliteColumnExistsAsync(db, "positions", "IsManaged"))
    {
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE "positions"
            ADD COLUMN "IsManaged" INTEGER NOT NULL DEFAULT 0;
            """);
    }

    await db.Database.ExecuteSqlRawAsync("""
        UPDATE "positions"
        SET "IsManaged" = 1
        WHERE "Symbol" IN (
            SELECT DISTINCT "Symbol"
            FROM "orders"
            WHERE "Status" NOT IN ('rejected', 'cancelled')
              AND "OrderType" NOT LIKE '%close%'
              AND "OrderType" NOT LIKE '%flatten%'
              AND "OrderType" NOT LIKE '%unmanaged%'
        );
        """);
}

static async Task<bool> SqliteColumnExistsAsync(TradingDbContext db, string tableName, string columnName)
{
    var connection = db.Database.GetDbConnection();
    var shouldClose = connection.State == System.Data.ConnectionState.Closed;
    if (shouldClose)
    {
        await connection.OpenAsync();
    }

    try
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""PRAGMA table_info("{tableName.Replace("\"", "\"\"", StringComparison.Ordinal)}");""";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (string.Equals(reader["name"]?.ToString(), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
    finally
    {
        if (shouldClose)
        {
            await connection.CloseAsync();
        }
    }
}

static async Task<HashSet<string>> GetManagedPositionKeysAsync(TradingDbContext db, CancellationToken cancellationToken)
{
    var managedPositionSymbols = await db.Positions
        .AsNoTracking()
        .Where(position => position.IsManaged)
        .Select(position => position.Symbol)
        .ToListAsync(cancellationToken);

    var managedOrderSymbols = await db.Orders
        .AsNoTracking()
        .Where(order => order.Status != "rejected" && order.Status != "cancelled")
        .Where(order => !order.OrderType.Contains("close") && !order.OrderType.Contains("flatten") && !order.OrderType.Contains("unmanaged"))
        .Select(order => order.Symbol)
        .Distinct()
        .ToListAsync(cancellationToken);

    return managedPositionSymbols
        .Concat(managedOrderSymbols)
        .Select(ToOwnershipKey)
        .Where(key => !string.IsNullOrWhiteSpace(key))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

static string ToOwnershipKey(string symbol)
{
    var normalized = (symbol ?? "").Trim().ToUpperInvariant();
    if (string.IsNullOrWhiteSpace(normalized))
    {
        return "";
    }

    if (normalized.Contains(':'))
    {
        normalized = normalized[(normalized.LastIndexOf(':') + 1)..];
    }

    normalized = normalized.TrimStart('/').Replace(" ", "", StringComparison.Ordinal);
    if (normalized.EndsWith("1!", StringComparison.OrdinalIgnoreCase))
    {
        normalized = normalized[..^2];
    }

    foreach (var root in new[] { "MNQ", "MES", "NQ", "ES" })
    {
        if (normalized.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        if (normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && normalized.Length > root.Length + 1
            && "FGHJKMNQUVXZ".Contains(normalized[root.Length], StringComparison.Ordinal)
            && char.IsDigit(normalized[^1]))
        {
            return root;
        }
    }

    return normalized;
}

static DateOnly? ParseDateOnly(string? value)
{
    return DateOnly.TryParse(value, out var parsed) ? parsed : null;
}

static async Task<IResult> ProcessMarketOrderAsync(MarketOrderRequest request, TradingDbContext db, IBrokerAdapter brokerAdapter, RiskSettingsStore riskSettingsStore, DailyPerformanceStore dailyPerformanceStore, IHubContext<TradingHub> hub)
{
    var validation = await ValidateMarketOrderAsync(request, db, riskSettingsStore, dailyPerformanceStore);
    if (validation.Errors.Count > 0)
    {
        db.AuditLogs.Add(AuditLogRecord.BrokerAction(
            "market_order.rejected",
            $"Market order rejected: {string.Join("; ", validation.Errors)}"));
        await db.SaveChangesAsync();

        return Results.Ok(new MarketOrderResponse(
            Ok: false,
            Status: "rejected_by_risk",
            Message: string.Join("; ", validation.Errors),
            Order: null,
            Position: null));
    }

    var brokerContracts = validation.Contracts!.Value;
    if (request.AttachProtection == true)
    {
        var existingPosition = await db.Positions
            .AsNoTracking()
            .SingleOrDefaultAsync(position => position.Symbol == validation.Symbol);

        if (existingPosition is not null
            && !existingPosition.Direction.Equals(validation.Direction!, StringComparison.OrdinalIgnoreCase))
        {
            brokerContracts += existingPosition.Quantity;
            db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                "market_order.reverse_requested",
                $"Market order for {validation.Symbol} {validation.Direction} {validation.Contracts.Value} will reverse existing {existingPosition.Direction} {existingPosition.Quantity}; broker quantity={brokerContracts}"));
        }
    }

    var result = await brokerAdapter.PlaceMarketOrderAsync(
        validation.Symbol!,
        validation.Direction!,
        brokerContracts,
        request.ReferencePrice,
        db,
        request.AttachProtection == true,
        request.ProtectionDistance);

    await db.SaveChangesAsync();
    await BroadcastTradingUpdateAsync(hub, result.Ok ? "market_order.created" : "market_order.rejected", validation.Symbol);

    return Results.Ok(new MarketOrderResponse(
        Ok: result.Ok,
        Status: result.Status,
        Message: result.Message,
        Order: result.Order,
        Position: result.Position));
}

static async Task<MarketOrderValidationResult> ValidateMarketOrderAsync(MarketOrderRequest request, TradingDbContext db, RiskSettingsStore riskSettingsStore, DailyPerformanceStore dailyPerformanceStore)
{
    var errors = new List<string>();
    var symbol = request.Symbol?.Trim().ToUpperInvariant();
    var direction = request.Direction?.Trim().ToUpperInvariant();
    var contracts = request.Contracts;
    var settings = riskSettingsStore.Get();

    if (string.IsNullOrWhiteSpace(symbol))
    {
        errors.Add("symbol is required");
    }

    if (direction is not ("LONG" or "SHORT"))
    {
        errors.Add("direction must be LONG or SHORT");
    }

    if (contracts is null or <= 0)
    {
        errors.Add("contracts must be a positive integer");
    }

    if (!settings.EnableAutoTrading)
    {
        errors.Add("Auto trading is disabled");
    }

    if (settings.TradingLocked)
    {
        errors.Add("Trading is locked");
    }

    if (settings.EmergencyStopActive)
    {
        errors.Add("Emergency stop is active");
    }

    if (settings.MaxContractsPerSignal <= 0)
    {
        errors.Add("Risk setting MaxContractsPerSignal must be greater than zero");
    }
    else if (contracts > settings.MaxContractsPerSignal)
    {
        errors.Add($"Contracts {contracts} exceeds max {settings.MaxContractsPerSignal}");
    }

    var allowedSymbols = settings.AllowedSymbols
        .Where(item => !string.IsNullOrWhiteSpace(item))
        .Select(item => item.Trim().ToUpperInvariant())
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    if (!string.IsNullOrWhiteSpace(symbol) && allowedSymbols.Count > 0 && !allowedSymbols.Contains(symbol))
    {
        errors.Add($"Symbol {symbol} is not allowed");
    }

    if (!settings.AllowPositionStacking && !string.IsNullOrWhiteSpace(symbol))
    {
        var hasOpenPosition = await db.Positions.AnyAsync(position => position.Symbol == symbol);
        if (hasOpenPosition)
        {
            errors.Add($"Open position already exists for {symbol}");
        }
    }

    if (settings.MaxLossPerTrade > 0 && request.AttachProtection == true && request.ProtectionDistance is > 0 && contracts is > 0 && !string.IsNullOrWhiteSpace(symbol))
    {
        var estimatedLoss = request.ProtectionDistance.Value * contracts.Value * GetPointValue(symbol);
        if (estimatedLoss > settings.MaxLossPerTrade)
        {
            errors.Add($"Estimated loss {estimatedLoss:0.##} exceeds max loss per trade {settings.MaxLossPerTrade:0.##}");
        }
    }

    if (settings.MaxDailyLoss > 0 && request.AttachProtection == true && request.ProtectionDistance is > 0 && contracts is > 0 && !string.IsNullOrWhiteSpace(symbol))
    {
        var projectedLoss = request.ProtectionDistance.Value * contracts.Value * GetPointValue(symbol);
        var today = await GetDailyPerformanceAsync(db, DateOnly.FromDateTime(DateTime.Now));
        var currentLoss = Math.Max(0m, -today.RealizedPnl);
        if (currentLoss >= settings.MaxDailyLoss)
        {
            errors.Add($"Daily loss {currentLoss:0.##} reached max daily loss {settings.MaxDailyLoss:0.##}");
        }
        else if (currentLoss + projectedLoss > settings.MaxDailyLoss)
        {
            errors.Add($"Projected daily loss {(currentLoss + projectedLoss):0.##} exceeds max daily loss {settings.MaxDailyLoss:0.##}");
        }
    }

    return new MarketOrderValidationResult(symbol, direction, contracts, errors);
}

static decimal GetPointValue(string symbol)
{
    var normalized = symbol.Trim().ToUpperInvariant().Replace("1!", "", StringComparison.OrdinalIgnoreCase);
    return normalized switch
    {
        "MNQ" => 2m,
        "NQ" => 20m,
        "MES" => 5m,
        "ES" => 50m,
        _ => 1m
    };
}

static async Task<DailyPerformanceSnapshot> GetDailyPerformanceAsync(TradingDbContext db, DateOnly date)
{
    var closedPositions = await db.ClosedPositions.ToListAsync();
    var todayClosedPositions = closedPositions
        .Where(position => DateOnly.FromDateTime(position.ClosedAt.LocalDateTime) == date)
        .ToList();

    return new DailyPerformanceSnapshot(
        date,
        todayClosedPositions.Sum(position => position.RealizedPnl ?? 0m),
        todayClosedPositions.Count);
}

static async Task<IResult> ProcessSignalAsync(TradingSignalRequest request, bool isManualTrade, TradingDbContext db, IBrokerAdapter brokerAdapter, RiskValidator riskValidator, RiskSettingsStore riskSettingsStore, IMarketDataProvider marketDataProvider, IHubContext<TradingHub> hub)
{
    var validation = TradingSignalValidator.Validate(request);
    if (!validation.Ok)
    {
        return Results.BadRequest(new ValidationErrorResponse(validation.Errors));
    }

    var normalized = validation.Signal!;
    var signal = TradingSignalRecord.FromRequest(normalized);
    var riskSettings = riskSettingsStore.Get();
    var calculatedContracts = CalculateSignalContracts(signal, riskSettings);
    if (calculatedContracts <= 0)
    {
        signal.Status = "rejected_by_risk";
        db.Signals.Add(signal);
        db.AuditLogs.Add(AuditLogRecord.SignalAccepted(signal));
        db.AuditLogs.Add(AuditLogRecord.RiskRejected(signal, ["Max loss per trade is too low for one contract at the signal stop distance"]));
        await db.SaveChangesAsync();
        await BroadcastTradingUpdateAsync(hub, isManualTrade ? "manual_trade.rejected" : "signal.rejected", signal.Symbol);
        return Results.Created($"/api/signals/{signal.Id}", TradingSignalResponse.FromRecord(signal));
    }

    signal.Contracts = calculatedContracts;

    db.Signals.Add(signal);
    db.AuditLogs.Add(AuditLogRecord.SignalAccepted(signal));
    await db.SaveChangesAsync();

    if (isManualTrade)
    {
        db.AuditLogs.Add(AuditLogRecord.ManualTradeSubmitted(signal));
    }

    try
    {
    var riskValidation = await riskValidator.ValidateEntrySignalAsync(signal, db);
    if (!riskValidation.IsApproved)
    {
        signal.Status = "rejected_by_risk";
        db.AuditLogs.Add(AuditLogRecord.RiskRejected(signal, riskValidation.Reasons));
        await db.SaveChangesAsync();

        await BroadcastTradingUpdateAsync(hub, isManualTrade ? "manual_trade.rejected" : "signal.rejected", signal.Symbol);

        return Results.Created($"/api/signals/{signal.Id}", TradingSignalResponse.FromRecord(signal));
    }

    db.AuditLogs.Add(AuditLogRecord.RiskApproved(signal));
    if (riskSettings.TestMode)
    {
        const decimal testProtectionDistance = 100m;
        var result = await brokerAdapter.PlaceMarketOrderAsync(
            signal.Symbol,
            signal.Direction,
            signal.Contracts,
            signal.EntryPrice,
            db,
            attachProtection: true,
            protectionDistance: testProtectionDistance);

        signal.Status = result.Ok
            ? "test_market_order_sent"
            : result.Status == "blocked" ? "broker_blocked" : "test_market_order_failed";
        db.AuditLogs.Add(AuditLogRecord.BrokerAction(
            result.Ok ? "test_mode.market_order_sent" : "test_mode.market_order_failed",
            $"Test mode converted signal {signal.Id} {signal.Symbol} {signal.Direction} {signal.Contracts} to market order with {testProtectionDistance:0.##} point SL/TP: {result.Message}"));
    }
    else
    {
        var currentPriceResult = await TryGetCurrentPriceAsync(signal.Symbol, marketDataProvider);
        if (currentPriceResult.Price is null)
        {
            signal.Status = "rejected_by_risk";
            db.AuditLogs.Add(AuditLogRecord.RiskRejected(signal, [$"Current price unavailable: {currentPriceResult.Error}"]));
        }
        else if (riskSettings.MaxEntryPriceDeviationPoints > 0
            && Math.Abs(signal.EntryPrice - currentPriceResult.Price.Value) > riskSettings.MaxEntryPriceDeviationPoints)
        {
            signal.Status = "ignored_entry_price_too_far";
            db.AuditLogs.Add(AuditLogRecord.RiskRejected(signal, [$"Entry price {signal.EntryPrice} is {Math.Abs(signal.EntryPrice - currentPriceResult.Price.Value):0.##} points from current price {currentPriceResult.Price.Value:0.##}, max allowed {riskSettings.MaxEntryPriceDeviationPoints:0.##}"]));
        }
        else
        {
            var result = await brokerAdapter.PlaceMarketOrderAsync(
                signal.Symbol,
                signal.Direction,
                signal.Contracts,
                currentPriceResult.Price,
                db,
                attachProtection: true,
                protectionDistance: null,
                stopLoss: signal.StopLoss,
                takeProfit: signal.TakeProfit1);

            signal.Status = result.Ok
                ? "market_order_with_signal_protection_sent"
                : result.Status == "blocked" ? "broker_blocked" : "market_order_failed";
            db.AuditLogs.Add(AuditLogRecord.BrokerAction(
                result.Ok ? "signal.market_order_sent" : "signal.market_order_failed",
                $"Signal {signal.Id} {signal.Symbol} {signal.Direction} {signal.Contracts} converted to market order with signal SL={signal.StopLoss} TP1={signal.TakeProfit1}: {result.Message}"));
        }
    }
    await db.SaveChangesAsync();

    var eventType = signal.Status == "broker_blocked"
        ? isManualTrade ? "manual_trade.broker_blocked" : "signal.broker_blocked"
        : signal.Status is "rejected_by_risk" or "ignored_entry_price_too_far"
            ? isManualTrade ? "manual_trade.rejected" : "signal.rejected"
            : isManualTrade ? "manual_trade.created" : "signal.created";

    await BroadcastTradingUpdateAsync(hub, eventType, signal.Symbol);

    return Results.Created($"/api/signals/{signal.Id}", TradingSignalResponse.FromRecord(signal));
    }
    catch (Exception error)
    {
        signal.Status = "broker_blocked";
        db.AuditLogs.Add(AuditLogRecord.BrokerAction(
            "signal.processing_failed",
            $"Signal {signal.Id} {signal.Symbol} failed after acceptance: {error.Message}"));
        await db.SaveChangesAsync();

        await BroadcastTradingUpdateAsync(hub, isManualTrade ? "manual_trade.broker_blocked" : "signal.broker_blocked", signal.Symbol);

        return Results.Created($"/api/signals/{signal.Id}", TradingSignalResponse.FromRecord(signal));
    }
}

static int CalculateSignalContracts(TradingSignalRecord signal, RiskSettings settings)
{
    if (settings.MaxLossPerTrade <= 0)
    {
        return Math.Min(signal.Contracts, settings.MaxContractsPerSignal);
    }

    var stopDistance = settings.TestMode
        ? 100m
        : Math.Abs(signal.EntryPrice - signal.StopLoss);
    if (stopDistance <= 0)
    {
        return 0;
    }

    var riskPerContract = stopDistance * GetPointValue(signal.Symbol);
    if (riskPerContract <= 0)
    {
        return 0;
    }

    var calculated = (int)Math.Floor(settings.MaxLossPerTrade / riskPerContract);
    return Math.Min(calculated, settings.MaxContractsPerSignal);
}

static async Task<(decimal? Price, string? Error)> TryGetCurrentPriceAsync(string symbol, IMarketDataProvider marketDataProvider)
{
    try
    {
        var candles = await marketDataProvider.GetCandlesAsync(symbol, "1m");
        var latest = candles.LastOrDefault();
        return latest is null
            ? (null, "no candles returned")
            : (latest.Close, null);
    }
    catch (Exception error) when (error is not OperationCanceledException)
    {
        return (null, error.Message);
    }
}

sealed record MarketOrderValidationResult(
    string? Symbol,
    string? Direction,
    int? Contracts,
    List<string> Errors
);
