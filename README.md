# Deploy .NET 8 API lên VPS với Docker & Coolify

> Stack: Ubuntu 22.04 · Coolify · Docker · .NET 8 Minimal API · EF Core  
> Hai nhánh: **A** = SQL Server (tự quản lý) · **B** = PostgreSQL (Coolify quản lý)

---

## Phần 1 — Setup VPS từ đầu

### 1.1 Bảo mật cơ bản ngay sau khi có VPS

```bash
# Đăng nhập lần đầu bằng root
ssh root@103.170.122.180

# Tạo user thường — không làm mọi thứ bằng root
adduser deploy
usermod -aG sudo deploy

# Copy SSH key sang user mới
rsync --archive --chown=deploy:deploy ~/.ssh /home/deploy

# Đăng xuất rồi đăng nhập lại bằng user deploy
exit
ssh deploy@103.170.122.180
```

### 1.2 Firewall — chỉ mở đúng port cần thiết

```bash
sudo apt update && sudo apt upgrade -y
sudo apt install -y ufw curl git

# Mặc định: chặn tất cả inbound, cho phép tất cả outbound
sudo ufw default deny incoming
sudo ufw default allow outgoing

# Chỉ mở 3 port:
sudo ufw allow 22/tcp       # SSH
sudo ufw allow 80/tcp       # HTTP (Traefik redirect sang HTTPS)
sudo ufw allow 443/tcp      # HTTPS

# Port 8000 chỉ mở tạm để setup Coolify, sau đó đóng lại
sudo ufw allow 8000/tcp

sudo ufw enable
sudo ufw status
```

> **Lưu ý quan trọng**: Port database (1433, 5432) KHÔNG bao giờ mở ra ngoài.  
> Container giao tiếp với nhau qua Docker internal network, không qua host port.

### 1.3 Cài Docker

```bash
# Cài Docker Engine (không dùng snap — hay có vấn đề trên Ubuntu)
curl -fsSL https://get.docker.com | sh
sudo usermod -aG docker deploy

# Đăng xuất rồi đăng nhập lại để nhóm docker có hiệu lực
exit
ssh deploy@103.170.122.180

# Kiểm tra
docker --version
docker compose version
```

### 1.4 Cài Coolify

```bash
curl -fsSL https://cdn.coollabs.io/coolify/install.sh | bash
```

Truy cập `http://103.170.122.180:8000` để tạo tài khoản admin.  
Sau khi setup xong Coolify, **đóng port 8000 lại**:

```bash
sudo ufw delete allow 8000/tcp
sudo ufw reload
```

Coolify sẽ được truy cập qua HTTPS domain riêng sau khi cấu hình Traefik.

---

## Phần 2 — Tổ chức thư mục trên VPS

Làm **một lần duy nhất** ngay sau khi cài xong Docker. Đây là nền tảng cho mọi service về sau.

```bash
# ── Toàn bộ data của mọi service ────────────────────────────────
sudo mkdir -p /srv/databases/postgres/{data,logs,config}
sudo mkdir -p /srv/databases/mysql/{data,logs,config}
sudo mkdir -p /srv/databases/sqlserver/{data,logs,config}
sudo mkdir -p /srv/databases/oracle/{data,logs,config}

sudo mkdir -p /srv/apps/coolify/{data,ssh,proxy}

sudo mkdir -p /srv/backups/databases/{postgres,mysql,sqlserver,oracle}
sudo mkdir -p /srv/backups/scripts
sudo mkdir -p /srv/backups/logs

# ── Phân quyền theo UID của process bên trong container ─────────
#
# Tại sao phải đúng UID?
# Docker mount thư mục từ host vào container.
# Process trong container chạy với UID cố định (không phải tên user).
# Nếu UID không khớp → permission denied → container không start được.
#
# PostgreSQL process  → UID 999
# MySQL process       → UID 999
# SQL Server process  → UID 10001, GID 0
# Oracle process      → UID 54321

sudo chown -R 999:999    /srv/databases/postgres
sudo chown -R 999:999    /srv/databases/mysql
sudo chown -R 10001:0    /srv/databases/sqlserver
sudo chown -R 54321:54321 /srv/databases/oracle

# data/ cần 700 — chỉ process của DB đọc được, không ai khác
sudo chmod 700 /srv/databases/postgres/data
sudo chmod 700 /srv/databases/mysql/data
sudo chmod 700 /srv/databases/sqlserver/data
sudo chmod 700 /srv/databases/oracle/data

# scripts/ cần execute
sudo chmod +x /srv/backups/scripts

# Kiểm tra lại
ls -la /srv/databases/
ls -la /srv/databases/postgres/
```

Output mong đợi:
```
drwx------ 2 999    999    ... data/
drwxr-xr-x 2 999    999    ... logs/
drwxr-xr-x 2 999    999    ... config/
```

---

## Phần 3 — Source code .NET 8

Chỉ `Program.cs` + EF Core. Đủ để học deploy, không rườm rà.

### 3.1 Cấu trúc project

```
product-api/
├── src/
│   └── ProductApi/
│       ├── ProductApi.csproj
│       ├── Program.cs            ← tất cả logic ở đây
│       ├── appsettings.json
│       └── Migrations/           ← EF Core tự generate, commit lên git
├── Dockerfile
├── .dockerignore
├── docker-compose.sqlserver.yml  ← Scenario A
├── docker-compose.postgres.yml   ← Scenario B (local dev)
├── .env.example                  ← template, commit lên git
├── .gitignore
└── .github/
    └── workflows/
        └── deploy.yml
```

### 3.2 Tạo project

```bash
dotnet new webapi -n ProductApi --framework net8.0 -o src/ProductApi
cd src/ProductApi
rm -rf Controllers WeatherForecast.cs

# Scenario A — SQL Server
dotnet add package Microsoft.EntityFrameworkCore.SqlServer
dotnet add package Microsoft.EntityFrameworkCore.Tools

# Scenario B — PostgreSQL (dùng thay cho dòng trên)
# dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL
# dotnet add package Microsoft.EntityFrameworkCore.Tools
```

### 3.3 Program.cs

```csharp
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ── Database ──────────────────────────────────────────────────────
var connStr = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("Missing connection string 'Default'");

// Scenario A — SQL Server:
builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseSqlServer(connStr, o => o.EnableRetryOnFailure(5)));

// Scenario B — PostgreSQL (thay thế 2 dòng trên):
// builder.Services.AddDbContext<AppDbContext>(opt =>
//     opt.UseNpgsql(connStr, o => o.EnableRetryOnFailure(5)));

// ── Swagger ───────────────────────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ── Health check ──────────────────────────────────────────────────
builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>();

var app = builder.Build();

// ── Migrate khi startup ───────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
}

app.UseSwagger();
app.UseSwaggerUI();

app.MapHealthChecks("/health");

// ── Endpoints ─────────────────────────────────────────────────────
app.MapGet("/products", async (AppDbContext db) =>
    await db.Products.Where(p => p.IsActive).ToListAsync());

app.MapGet("/products/{id:guid}", async (Guid id, AppDbContext db) =>
    await db.Products.FindAsync(id) is Product p
        ? Results.Ok(p)
        : Results.NotFound());

app.MapPost("/products", async (ProductRequest req, AppDbContext db) =>
{
    var product = new Product
    {
        Id          = Guid.NewGuid(),
        Name        = req.Name,
        Price       = req.Price,
        IsActive    = true,
        CreatedAt   = DateTime.UtcNow,
    };
    db.Products.Add(product);
    await db.SaveChangesAsync();
    return Results.Created($"/products/{product.Id}", product);
});

app.MapPut("/products/{id:guid}", async (Guid id, ProductRequest req, AppDbContext db) =>
{
    var product = await db.Products.FindAsync(id);
    if (product is null) return Results.NotFound();

    product.Name      = req.Name;
    product.Price     = req.Price;
    product.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.Ok(product);
});

app.MapDelete("/products/{id:guid}", async (Guid id, AppDbContext db) =>
{
    var product = await db.Products.FindAsync(id);
    if (product is null) return Results.NotFound();

    product.IsActive  = false;   // soft delete — không xóa khỏi DB
    product.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();
    return Results.NoContent();
});

app.Run();

// ── Models ────────────────────────────────────────────────────────
class Product
{
    public Guid      Id          { get; set; }
    public string    Name        { get; set; } = string.Empty;
    public decimal   Price       { get; set; }
    public bool      IsActive    { get; set; }
    public DateTime  CreatedAt   { get; set; }
    public DateTime? UpdatedAt   { get; set; }
}

record ProductRequest(string Name, decimal Price);

class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) {}
    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Product>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Name).IsRequired().HasMaxLength(200);
            e.Property(p => p.Price).HasPrecision(18, 2);
            e.HasIndex(p => p.IsActive);
        });
    }
}
```

### 3.4 appsettings.json

```json
{
  "ConnectionStrings": {
    "Default": ""
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore.Database.Command": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

`Default` để trống — giá trị thực inject qua biến môi trường khi chạy trên server.  
Không bao giờ commit connection string thật lên git.

### 3.5 Tạo EF Core Migration

```bash
cd src/ProductApi
dotnet ef migrations add InitialCreate --output-dir Migrations

# Kiểm tra SQL sẽ chạy (không apply vào DB)
dotnet ef migrations script

# Commit migrations vào git — đây là "lịch sử schema" của project
git add Migrations/
git commit -m "feat: add initial product migration"
```

---

## Phần 4 — Dockerfile

```dockerfile
# ── Stage 1: Build ────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj trước để tận dụng layer cache
# Nếu chỉ đổi code, layer restore không chạy lại → build nhanh hơn
COPY src/ProductApi/ProductApi.csproj src/ProductApi/
RUN dotnet restore src/ProductApi/ProductApi.csproj

COPY src/ src/
WORKDIR /src/src/ProductApi
RUN dotnet publish -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ── Stage 2: Runtime ──────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# Không chạy với root — giảm attack surface
RUN adduser --disabled-password --gecos "" appuser

WORKDIR /app
COPY --from=build /app/publish .

RUN mkdir -p logs && chown appuser:appuser logs
USER appuser

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "ProductApi.dll"]
```

**.dockerignore** — tránh copy rác vào build context:

```
**/.git
**/bin
**/obj
**/.vs
**/.vscode
**/*.md
**/docker-compose*
**/.env
**/logs
```

---

## Phần 5 — Docker Compose

### Naming convention cho network

| Tên | Dùng khi nào |
|-----|-------------|
| `internal` | Network nội bộ giữa các container, không expose ra ngoài |
| `proxy` | Network giữa container app và reverse proxy (Traefik) |
| `external` | Network kết nối ra ngoài hoặc chia sẻ giữa nhiều stack |

Coolify tạo sẵn network `proxy` (tên thực tế: `coolify`) cho Traefik. Các container app cần join network này để nhận traffic từ Traefik.

### 5.1 Scenario A — SQL Server

```yaml
# docker-compose.sqlserver.yml
name: product-sqlserver     # tên stack — hiển thị trong "docker compose ls"

networks:
  internal:                 # container nói chuyện nội bộ với nhau
    driver: bridge
    internal: true          # chặn hoàn toàn traffic ra ngoài host
  proxy:
    external: true          # join network của Coolify/Traefik đã tạo sẵn
    name: coolify           # tên thực tế Coolify đặt cho network Traefik

services:

  sqlserver:
    image: mcr.microsoft.com/mssql/server:2022-latest
    container_name: product-sqlserver
    restart: unless-stopped
    networks:
      - internal            # chỉ nội bộ, không cần proxy
    environment:
      ACCEPT_EULA: "Y"
      MSSQL_SA_PASSWORD: ${SA_PASSWORD}
      MSSQL_PID: "Developer"
    volumes:
      - /srv/databases/sqlserver/data:/var/opt/mssql/data
      - /srv/databases/sqlserver/logs:/var/opt/mssql/log
      - /srv/databases/sqlserver/config:/var/opt/mssql/config
    # KHÔNG có "ports:" — SQL Server không bao giờ expose ra ngoài
    healthcheck:
      test: ["CMD-SHELL",
        "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"$$SA_PASSWORD\" -No -Q 'SELECT 1' || exit 1"]
      interval: 15s
      timeout: 5s
      retries: 10
      start_period: 30s     # SQL Server khởi động chậm

  api:
    image: ghcr.io/${GITHUB_USERNAME}/product-api:${IMAGE_TAG:-latest}
    container_name: product-api
    restart: unless-stopped
    networks:
      - internal            # kết nối với sqlserver
      - proxy               # nhận traffic từ Traefik
    depends_on:
      sqlserver:
        condition: service_healthy
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      ASPNETCORE_URLS: http://+:8080
      ConnectionStrings__Default: >-
        Server=sqlserver,1433;
        Database=${DB_NAME:-ProductDb};
        User Id=sa;
        Password=${SA_PASSWORD};
        TrustServerCertificate=True;
        MultipleActiveResultSets=True
    # KHÔNG có "ports:" — Traefik sẽ route traffic vào
    labels:
      - "traefik.enable=true"
      - "traefik.http.routers.product-api.rule=Host(`${API_DOMAIN}`)"
      - "traefik.http.routers.product-api.entrypoints=websecure"
      - "traefik.http.routers.product-api.tls.certresolver=letsencrypt"
      - "traefik.http.services.product-api.loadbalancer.server.port=8080"
    healthcheck:
      test: ["CMD-SHELL", "curl -f http://localhost:8080/health || exit 1"]
      interval: 30s
      timeout: 5s
      retries: 3
      start_period: 15s
```

### 5.2 Scenario B — PostgreSQL (local dev)

File này **chỉ dùng để dev local**. Trên production, PostgreSQL do Coolify tạo và quản lý riêng.

```yaml
# docker-compose.postgres.yml
name: product-postgres-dev

networks:
  internal:
    driver: bridge
    internal: true

services:

  postgres:
    image: postgres:16-alpine
    container_name: product-postgres-dev
    restart: unless-stopped
    networks:
      - internal
    environment:
      POSTGRES_DB: ${DB_NAME:-ProductDb}
      POSTGRES_USER: ${DB_USER:-productuser}
      POSTGRES_PASSWORD: ${DB_PASSWORD}
      PGDATA: /var/lib/postgresql/data/pgdata
    volumes:
      - /srv/databases/postgres/data:/var/lib/postgresql/data
      - /srv/databases/postgres/logs:/var/log/postgresql
    ports:
      - "127.0.0.1:5432:5432"   # chỉ bind localhost — không phơi ra 0.0.0.0
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U ${DB_USER:-productuser} -d ${DB_NAME:-ProductDb}"]
      interval: 5s
      timeout: 3s
      retries: 10

  api:
    build:
      context: .
      dockerfile: Dockerfile
    container_name: product-api-dev
    restart: unless-stopped
    networks:
      - internal
    depends_on:
      postgres:
        condition: service_healthy
    environment:
      ASPNETCORE_ENVIRONMENT: Development
      ASPNETCORE_URLS: http://+:8080
      ConnectionStrings__Default: >-
        Host=postgres;
        Port=5432;
        Database=${DB_NAME:-ProductDb};
        Username=${DB_USER:-productuser};
        Password=${DB_PASSWORD}
    ports:
      - "127.0.0.1:5000:8080"   # local dev: bind localhost để test qua browser
```

### 5.3 .env.example — commit file này lên git

```bash
# .env.example — đây là template, không chứa giá trị thật
# Copy thành .env rồi điền giá trị thật, KHÔNG commit .env lên git

# Database
SA_PASSWORD=
DB_NAME=ProductDb
DB_USER=productuser
DB_PASSWORD=

# Docker image
GITHUB_USERNAME=
IMAGE_TAG=latest

# Domain
API_DOMAIN=api.yourdomain.com
```

Thêm `.env` vào `.gitignore`:
```
.env
*.env.local
```

---

## Phần 6 — Deploy lên Coolify

### 6.1 Scenario A — Coolify chạy toàn bộ stack từ docker-compose

```
Coolify UI
→ New Resource
→ Docker Compose
→ Source: GitHub repo
→ Compose file: docker-compose.sqlserver.yml
→ Branch: main
```

Trong **Environment Variables** của service, thêm:
```
SA_PASSWORD     = <password thật>
DB_NAME         = ProductDb
GITHUB_USERNAME = <your-github-username>
IMAGE_TAG       = latest
API_DOMAIN      = api.yourdomain.com
```

### 6.2 Scenario B — PostgreSQL tạo riêng trong Coolify, API deploy riêng

**Bước 1** — Tạo PostgreSQL trong Coolify:
```
Coolify → New Resource → Database → PostgreSQL 16
```

Trong phần cài đặt DB, override volume path:
```
Volume: /srv/databases/postgres/data → /var/lib/postgresql/data
```

Coolify sẽ hiện connection string nội bộ, dạng:
```
postgresql://productuser:password@postgres-container-name:5432/ProductDb
```

**Bước 2** — Deploy API:
```
Coolify → New Resource → Application
→ Source: GitHub Container Registry
→ Image: ghcr.io/<username>/product-api:latest
```

Environment variables cho API:
```
ASPNETCORE_ENVIRONMENT    = Production
ConnectionStrings__Default = Host=<postgres-host>;Port=5432;Database=ProductDb;Username=<user>;Password=<pass>
```

**Bước 3** — Kết nối cùng network:
```
Coolify → API service → Network
→ Connect to same network as PostgreSQL service
```

### 6.3 Trỏ DNS và cấu hình domain

Thêm A record vào DNS:
```
api.yourdomain.com  →  103.170.122.180
```

Trong Coolify → Application → Domains:
```
Domain: https://api.yourdomain.com
→ Coolify tự xin SSL cert từ Let's Encrypt qua Traefik
```

---

## Phần 7 — Kiểm tra sau deploy

```bash
# Xem container đang chạy
docker ps

# Xem log realtime
docker logs product-api -f --tail 50
docker logs product-sqlserver -f --tail 50

# Kiểm tra health
curl https://api.yourdomain.com/health

# Xem network — confirm container join đúng network
docker network inspect coolify
docker network inspect product-sqlserver_internal

# Xem volume mount — confirm đúng path
docker inspect product-sqlserver | grep -A 20 '"Mounts"'
```

---

## Phần 8 — Backup

### 8.1 Script backup SQL Server

```bash
# /srv/backups/scripts/backup-sqlserver.sh
#!/bin/bash
set -euo pipefail   # dừng ngay nếu có lỗi, không chạy tiếp

DATE=$(date +%Y%m%d_%H%M%S)
BACKUP_DIR="/srv/backups/databases/sqlserver"
DB_NAME="${DB_NAME:-ProductDb}"
CONTAINER="product-sqlserver"

echo "[$(date)] Bắt đầu backup SQL Server..."

docker exec "$CONTAINER" \
    /opt/mssql-tools18/bin/sqlcmd \
    -S localhost -U sa -P "$SA_PASSWORD" -No \
    -Q "BACKUP DATABASE [$DB_NAME]
        TO DISK = '/var/opt/mssql/backup/${DB_NAME}_${DATE}.bak'
        WITH FORMAT, COMPRESSION, STATS = 10"

# File bak nằm trong container, copy ra host
docker cp "$CONTAINER:/var/opt/mssql/backup/${DB_NAME}_${DATE}.bak" \
    "$BACKUP_DIR/${DB_NAME}_${DATE}.bak"

# Nén lại
gzip "$BACKUP_DIR/${DB_NAME}_${DATE}.bak"

# Giữ 7 ngày gần nhất
find "$BACKUP_DIR" -name "*.bak.gz" -mtime +7 -delete

echo "[$(date)] Xong: ${DB_NAME}_${DATE}.bak.gz"
```

### 8.2 Script backup PostgreSQL

```bash
# /srv/backups/scripts/backup-postgres.sh
#!/bin/bash
set -euo pipefail

DATE=$(date +%Y%m%d_%H%M%S)
BACKUP_DIR="/srv/backups/databases/postgres"
DB_NAME="${DB_NAME:-ProductDb}"
DB_USER="${DB_USER:-productuser}"
CONTAINER="product-postgres"

echo "[$(date)] Bắt đầu backup PostgreSQL..."

# pg_dump với custom format: nhỏ hơn SQL thuần, restore nhanh hơn
docker exec "$CONTAINER" \
    pg_dump -U "$DB_USER" -d "$DB_NAME" \
    --format=custom --compress=9 \
    --file="/tmp/${DB_NAME}_${DATE}.pgdump"

docker cp "$CONTAINER:/tmp/${DB_NAME}_${DATE}.pgdump" \
    "$BACKUP_DIR/${DB_NAME}_${DATE}.pgdump"

docker exec "$CONTAINER" rm "/tmp/${DB_NAME}_${DATE}.pgdump"

find "$BACKUP_DIR" -name "*.pgdump" -mtime +7 -delete

echo "[$(date)] Xong: ${DB_NAME}_${DATE}.pgdump"
```

### 8.3 Cấp quyền và đặt lịch chạy tự động

```bash
sudo chmod +x /srv/backups/scripts/backup-sqlserver.sh
sudo chmod +x /srv/backups/scripts/backup-postgres.sh

# Crontab — backup lúc 2:00 sáng mỗi ngày
crontab -e
```

Thêm vào:
```
0 2 * * * SA_PASSWORD=<pass> DB_NAME=ProductDb /srv/backups/scripts/backup-sqlserver.sh >> /srv/backups/logs/sqlserver.log 2>&1
0 2 * * * DB_NAME=ProductDb DB_USER=productuser /srv/backups/scripts/backup-postgres.sh >> /srv/backups/logs/postgres.log 2>&1
```

### 8.4 Restore khi cần

```bash
# Restore SQL Server
docker cp "$BACKUP_DIR/ProductDb_20240101_020000.bak.gz" product-sqlserver:/tmp/
docker exec product-sqlserver gunzip /tmp/ProductDb_20240101_020000.bak.gz
docker exec product-sqlserver \
    /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SA_PASSWORD" -No \
    -Q "RESTORE DATABASE [ProductDb] FROM DISK = '/tmp/ProductDb_20240101_020000.bak' WITH REPLACE"

# Restore PostgreSQL
docker cp "$BACKUP_DIR/ProductDb_20240101_020000.pgdump" product-postgres:/tmp/
docker exec product-postgres \
    pg_restore -U productuser -d ProductDb --clean --if-exists \
    /tmp/ProductDb_20240101_020000.pgdump
```

---

## Tóm tắt thứ tự làm

```
1. Setup VPS         → ufw, docker, user deploy
2. Tạo thư mục       → mkdir -p + chown + chmod (làm 1 lần)
3. Cài Coolify       → install.sh, đóng port 8000
4. Viết code         → Program.cs, EF Core migration
5. Dockerfile        → 2 stage build
6. docker-compose    → đặt network internal/proxy đúng
7. .env.example      → commit lên git, .env thật thì không commit
8. Deploy Coolify    → khai báo env vars trong Coolify UI
9. DNS + HTTPS       → A record + domain trong Coolify
10. Backup           → script + crontab
```