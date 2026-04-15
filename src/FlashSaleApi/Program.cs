using FlashSaleApi.Infrastructure.Data;
using FlashSaleApi.Infrastructure.Redis;
using FlashSaleApi.Middleware;
using FlashSaleApi.Repositories;
using FlashSaleApi.Repositories.Interfaces;
using FlashSaleApi.Services;
using FlashSaleApi.Services.Interfaces;
using FlashSaleApi.Workers;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Serilog;
using StackExchange.Redis;
using System.Reflection;
using System.Threading.RateLimiting;


// ────────────────────────────────────────────────────────────────────────────
// 1. Configure Serilog (logging)
//    Reads configuration from appsettings.json under "Serilog" section.
//    Sinks: console (pretty) + rolling file (logs/flashsale-.log)
// ────────────────────────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(new ConfigurationBuilder()
        .AddJsonFile("appsettings.json")
        .AddEnvironmentVariables()
        .Build())
    .Enrich.FromLogContext()
    .CreateLogger();

try
{
    Log.Information("Starting Flash Sale API...");

    var builder = WebApplication.CreateBuilder(args);

    // Use Serilog for ALL .NET logging (replaces default providers)
    builder.Host.UseSerilog();

    // ────────────────────────────────────────────────────────────────────────
    // 2. Database (PostgreSQL via EF Core + Npgsql)
    // ────────────────────────────────────────────────────────────────────────
    builder.Services.AddDbContext<AppDbContext>(options =>
        options.UseNpgsql(
            builder.Configuration.GetConnectionString("DefaultConnection"),
            npgsqlOptions => npgsqlOptions.EnableRetryOnFailure(maxRetryCount: 3)));

    // ────────────────────────────────────────────────────────────────────────
    // 3. Redis (StackExchange.Redis)
    //    - ConnectionMultiplexer is expensive to create; register as Singleton
    // ────────────────────────────────────────────────────────────────────────
    builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    {
        var connStr = builder.Configuration.GetConnectionString("Redis")
            ?? "localhost:6379";
        return ConnectionMultiplexer.Connect(connStr);
    });
    builder.Services.AddSingleton<IRedisService, RedisService>();

    // ────────────────────────────────────────────────────────────────────────
    // 4. Repositories & Services (Scoped = one instance per HTTP request)
    // ────────────────────────────────────────────────────────────────────────
    builder.Services.AddScoped<IFlashSaleRepository, FlashSaleRepository>();
    builder.Services.AddScoped<IOrderRepository, OrderRepository>();
    builder.Services.AddScoped<IFlashSaleService, FlashSaleService>();
    builder.Services.AddScoped<ICartService, CartService>();
    builder.Services.AddScoped<IOrderService, OrderService>();

    // ────────────────────────────────────────────────────────────────────────
    // 5. Background Worker (Singleton — runs for the lifetime of the app)
    // ────────────────────────────────────────────────────────────────────────
    builder.Services.AddHostedService<OrderProcessingWorker>();

    // ────────────────────────────────────────────────────────────────────────
    // 6. Rate Limiting
    //    - Fixed Window: 10 requests per 10 seconds per IP address
    //    - Applied only to POST /api/orders via [EnableRateLimiting("order-policy")]
    // ────────────────────────────────────────────────────────────────────────
    builder.Services.AddRateLimiter(options =>
    {
        options.OnRejected = async (context, ct) =>
        {
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await context.HttpContext.Response.WriteAsJsonAsync(new
            {
                status = 429,
                title  = "Too Many Requests",
                detail = "You have exceeded the order rate limit. Please wait and try again."
            }, ct);
        };

        options.AddFixedWindowLimiter("order-policy", limiterOptions =>
        {
            limiterOptions.Window              = TimeSpan.FromSeconds(10);
            limiterOptions.PermitLimit         = 10;
            limiterOptions.QueueLimit          = 0; // Reject immediately, no queuing
            limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        });
    });

    // ────────────────────────────────────────────────────────────────────────
    // 7. MVC Controllers & Caching
    // ────────────────────────────────────────────────────────────────────────
    builder.Services.AddMemoryCache();
    builder.Services.AddControllers();

    // ────────────────────────────────────────────────────────────────────────
    // 8. Swagger / OpenAPI
    //    - Reads XML doc comments for detailed endpoint descriptions
    // ────────────────────────────────────────────────────────────────────────
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        options.SwaggerDoc("v1", new OpenApiInfo
        {
            Title       = "Flash Sale API",
            Version     = "v1",
            Description = "Production-grade Flash Sale API with Redis-backed stock management, " +
                          "atomic Lua script decrement, idempotent order placement, " +
                          "and async background processing.",
            Contact = new OpenApiContact
            {
                Name  = "Flash Sale API",
                Email = "admin@flashsale.example.com"
            }
        });

        // Add X-User-Id header as a global parameter
        options.AddSecurityDefinition("UserIdHeader", new OpenApiSecurityScheme
        {
            Name        = "X-User-Id",
            In          = ParameterLocation.Header,
            Type        = SecuritySchemeType.ApiKey,
            Description = "User identifier for cart and order operations."
        });

        // Include XML documentation comments
        var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
        var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
        if (File.Exists(xmlPath))
            options.IncludeXmlComments(xmlPath);
    });

    // ────────────────────────────────────────────────────────────────────────
    // BUILD
    // ────────────────────────────────────────────────────────────────────────
    var app = builder.Build();

    // ────────────────────────────────────────────────────────────────────────
    // 9. Database Migration + Stock Seeding on Startup
    // ────────────────────────────────────────────────────────────────────────
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Auto-apply pending migrations (dev-friendly; use explicit migration in CI/CD)
        await db.Database.MigrateAsync();
        Log.Information("Database migration applied.");

        // Seed product stock into Redis
        var flashSaleService = scope.ServiceProvider.GetRequiredService<IFlashSaleService>();
        await flashSaleService.SeedStockToRedisAsync();
    }

    // ────────────────────────────────────────────────────────────────────────
    // 10. Middleware Pipeline (ORDER MATTERS)
    // ────────────────────────────────────────────────────────────────────────
    app.UseGlobalExceptionHandling();     // 1st: catch all unhandled exceptions
    app.UseSerilogRequestLogging();       // log every HTTP request

    // Enable Swagger in all environments for this demo project.
    // In a production deployment, add "Swagger:Enabled": false to appsettings.Production.json
    var swaggerEnabled = app.Configuration.GetValue("Swagger:Enabled", defaultValue: true);
    if (swaggerEnabled)
    {
        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "Flash Sale API v1");
            c.RoutePrefix = string.Empty; // Serve Swagger at root "/"
        });
    }

    app.UseHttpsRedirection();
    app.UseRateLimiter();
    app.MapControllers();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Flash Sale API terminated unexpectedly.");
}
finally
{
    Log.CloseAndFlush();
}
