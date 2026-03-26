using Microsoft.EntityFrameworkCore;
using ProductApi;
using ProductApi.Models;
using ProductApi.Reqs;

var builder = WebApplication.CreateBuilder(args);

// ── Database ──────────────────────────────────────────────────────
var connStr = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Missing connection string 'Default'");

// Scenario A — SQL Server:
builder.Services.AddDbContext<ApplicationDbContext>(opt =>
    opt.UseSqlServer(connStr, o => o.EnableRetryOnFailure(5)));

// Scenario B — PostgreSQL (thay thế 2 dòng trên):
// builder.Services.AddDbContext<AppDbContext>(opt =>
//     opt.UseNpgsql(connStr, o => o.EnableRetryOnFailure(5)));

// ── Swagger ───────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ── Health check ──────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddDbContextCheck<ApplicationDbContext>();

// Add services to the container.

//builder.Services.AddControllers();
//// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
//builder.Services.AddEndpointsApiExplorer();
//builder.Services.AddSwaggerGen();

var app = builder.Build();

// ── Migrate khi startup ───────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await db.Database.MigrateAsync();
}

// Configure the HTTP request pipeline.
//if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapHealthChecks("/health");

//app.UseHttpsRedirection();

//app.UseAuthorization();

app.MapControllers();

// ── Endpoints ─────────────────────────────────────────────────────
app.MapGet("/products", async (ApplicationDbContext db) =>
    await db.Products.Where(p => p.IsActive).ToListAsync());

app.MapGet("/products/{id:guid}", async (Guid id, ApplicationDbContext db) =>
    await db.Products.FindAsync(id) is Product p
        ? Results.Ok(p)
        : Results.NotFound());

app.MapPost("/products", async (ProductRequest req, ApplicationDbContext db) =>
{
    var product = new Product
    {
        Id = Guid.NewGuid(),
        Name = req.Name,
        Price = req.Price,
        IsActive = true,
        CreatedAt = DateTime.UtcNow,
    };
    db.Products.Add(product);
    await db.SaveChangesAsync();
    return Results.Created($"/products/{product.Id}", product);
});

app.MapPut("/products/{id:guid}", async (Guid id, ProductRequest req, ApplicationDbContext db) =>
{
    var product = await db.Products.FindAsync(id);
    if (product is null) return Results.NotFound();

    product.Name = req.Name;
    product.Price = req.Price;
    product.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(product);
});

app.MapDelete("/products/{id:guid}", async (Guid id, ApplicationDbContext db) =>
{
    var product = await db.Products.FindAsync(id);
    if (product is null) return Results.NotFound();

    product.IsActive = false;   // soft delete — không xóa khỏi DB
    product.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.Run();