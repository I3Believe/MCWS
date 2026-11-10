using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MCWS.Components;
using MCWS.Data;
using MCWS.Services;

var builder = WebApplication.CreateBuilder(args);

// 클라우드(OCI Linux) 외부 접속을 위해 기본 포트를 0.0.0.0:5000으로 바인딩 (환경변수 덮어쓰기 가능)
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls("http://0.0.0.0:5000");
}

// 리눅스 서버 재시작 시에도 ProtectedLocalStorage 암호화 키가 유지되도록 디스크에 키 보관
string keysPath = Path.Combine(Directory.GetCurrentDirectory(), "storage", "keys");
Directory.CreateDirectory(keysPath);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
    .SetApplicationName("MCWS");

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// SQLite 데이터베이스 연결 설정
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=app.db"));

// 백엔드 서비스 등록
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<AuthStateService>();
builder.Services.AddSingleton<ServerInstanceManager>();

var app = builder.Build();

// 서버 실행시 app.db파일과 5개 테이블이 없으면 자동 생성
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    // EnsureCreated()는 DB 파일이 이미 있으면 아무것도 안 함
    // -> 운영 중인 app.db에 새로 추가된 테이블/컬럼을 직접 보강 (간이 마이그레이션)
    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS AddonFiles (
            Id INTEGER NOT NULL CONSTRAINT PK_AddonFiles PRIMARY KEY AUTOINCREMENT,
            FileName TEXT NOT NULL,
            Type INTEGER NOT NULL,
            FileSizeBytes INTEGER NOT NULL,
            UploadedAt TEXT NOT NULL
        );");
    AddColumnIfMissing(db, "ServerTemplates", "LoaderType", "INTEGER NOT NULL DEFAULT 0");
    AddColumnIfMissing(db, "ServerInstances", "LoaderType", "INTEGER NOT NULL DEFAULT 0");
}

// 테이블에 컬럼이 없을 때만 ALTER TABLE로 추가 (기존 데이터는 DEFAULT 0 = PluginOnly 로 채워짐)
static void AddColumnIfMissing(AppDbContext db, string table, string column, string definition)
{
    var conn = db.Database.GetDbConnection();
    if (conn.State != System.Data.ConnectionState.Open) conn.Open();

    using var check = conn.CreateCommand();
    check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}';";
    var exists = Convert.ToInt32(check.ExecuteScalar()) > 0;
    if (exists) return;

    using var alter = conn.CreateCommand();
    alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
    alter.ExecuteNonQuery();
}

// 웹 서버(MCWS) 종료 시 실행 중인 모든 마인크래프트 자식 프로세스에 stop 명령을 보내 정상 종료 보장
var instanceManager = app.Services.GetRequiredService<ServerInstanceManager>();
app.Lifetime.ApplicationStopping.Register(() =>
{
    instanceManager.ShutdownAllAsync().GetAwaiter().GetResult();
});

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapGet("/download/{category}/{fileName}", (string category, string fileName, ServerInstanceManager manager) =>
{
    string? folderPath = category.ToLower() switch
    {
        "jars" => manager.JarsPath,
        "maps" => manager.MapsPath,
        "plugins" => manager.PluginsPath,
        "mods" => manager.ModsPath,
        _ => null
    };

    if (folderPath == null) return Results.NotFound("잘못된 카테고리입니다.");

    string safeName = Path.GetFileName(fileName);
    string fullPath = Path.Combine(folderPath, safeName);

    if(!File.Exists(fullPath)) return Results.NotFound("파일이 존재하지 않습니다.");

    return Results.File(fullPath, "application/octet-stream", safeName);
});

app.Run();
