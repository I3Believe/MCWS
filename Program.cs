using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MCWS_v2.Components;
using MCWS_v2.Data;
using MCWS_v2.Services;

var builder = WebApplication.CreateBuilder(args);

// 클라우드(OCI Linux) 외부 접속을 위해 기본 포트를 0.0.0.0:5000으로 바인딩
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls("http://0.0.0.0:5000");
}

// 리눅스 서버 재시작 시에도 세션 암호화 키가 유지되도록 디스크에 키 보관
string keysPath = Path.Combine(Directory.GetCurrentDirectory(), "storage", "keys");
Directory.CreateDirectory(keysPath);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath))
    .SetApplicationName("MCWS_v2");

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(options =>
    {
        // 대용량 월드 맵(.zip) 및 버킷(.jar) 업로드 시 소켓 끊김 방지 (최대 500MB 허용)
        options.MaximumReceiveMessageSize = 500L * 1024L * 1024L;
    });

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 500L * 1024L * 1024L;
});

// SQLite 데이터베이스 연결 설정
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=app.db"));

// 역할별로 분리된 백엔드 서비스 등록 (DI)
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<AuthStateService>();
builder.Services.AddScoped<AddonService>();
builder.Services.AddSingleton<SystemMetricsService>();
builder.Services.AddSingleton<StorageService>();
builder.Services.AddSingleton<ServerInstanceManager>();

var app = builder.Build();

// 서버 실행 시 app.db와 테이블 자동 생성 및 v1->v2 자동 마이그레이션
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    // v1에서 v2로 넘어올 때 WorldMaps 데이터를 AddonFiles(Type=2: World)로 자동 승계
    try
    {
        db.Database.ExecuteSqlRaw(@"
            INSERT INTO AddonFiles (FileName, Type, FileSizeBytes, UploadedAt)
            SELECT FileName, 2, FileSizeBytes, UploadedAt FROM WorldMaps;
            DROP TABLE WorldMaps;
        ");
    }
    catch
    {
        // 이미 승계 완료되었거나 WorldMaps 테이블이 없으면 자연스럽게 통과
    }
}

// 웹 서버 종료 시 실행 중인 모든 마인크래프트 자식 프로세스를 안전 종료
var instanceManager = app.Services.GetRequiredService<ServerInstanceManager>();
app.Lifetime.ApplicationStopping.Register(() =>
{
    instanceManager.ShutdownAllAsync().GetAwaiter().GetResult();
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found");
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// 파일 다운로드 엔드포인트 (이제 ServerInstanceManager가 아닌 StorageService가 전담)
app.MapGet("/download/{category}/{fileName}", (string category, string fileName, StorageService storage) =>
{
    string? folderPath = category.ToLower() switch
    {
        "jars" => storage.JarsPath,
        "maps" => storage.MapsPath,
        "plugins" => storage.PluginsPath,
        "mods" => storage.ModsPath,
        _ => null
    };

    if (folderPath == null) return Results.NotFound("잘못된 카테고리입니다.");

    string safeName = Path.GetFileName(fileName);
    string fullPath = Path.Combine(folderPath, safeName);

    if (!File.Exists(fullPath)) return Results.NotFound("파일이 존재하지 않습니다.");

    return Results.File(fullPath, "application/octet-stream", safeName);
});

app.Run();
