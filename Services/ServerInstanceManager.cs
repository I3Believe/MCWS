using System.Collections.Concurrent;
using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using MCWS.Data;
using MCWS.Models;

namespace MCWS.Services;

public class ServerInstanceManager
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConcurrentDictionary<int, ServerProcess> _processes = new();

    // 모든 파일이 저장될 기준 폴더 (실행 위치 아래 storage 폴더)
    public string BaseStoragePath => Path.Combine(Directory.GetCurrentDirectory(), "storage");
    public string JarsPath => Path.Combine(BaseStoragePath, "templates", "jars");
    public string MapsPath => Path.Combine(BaseStoragePath, "templates", "maps");
    public string PluginsPath => Path.Combine(BaseStoragePath, "templates", "plugins");
    public string ModsPath => Path.Combine(BaseStoragePath, "templates", "mods");
    public string InstancesPath => Path.Combine(BaseStoragePath, "instances");

    // 웹 화면으로 실시간 로그와 상태를 쏴주는 이벤트
    public event Action<int, ConsoleLogLine>? OnLogBroadcast;
    public event Action<int, ServerStatus>? OnStatusBroadcast;

    public ServerInstanceManager(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
        Directory.CreateDirectory(JarsPath);
        Directory.CreateDirectory(MapsPath);
        Directory.CreateDirectory(PluginsPath);
        Directory.CreateDirectory(ModsPath);

        Directory.CreateDirectory(InstancesPath);
    }

    public ServerStatus GetStatus(int instanceId)
    {
        return _processes.TryGetValue(instanceId, out var proc) ? proc.Status : ServerStatus.Offline;
    }

    public IReadOnlyList<ConsoleLogLine> GetLogs(int instanceId)
    {
        return _processes.TryGetValue(instanceId, out var proc) ? proc.GetRecentLogs() : Array.Empty<ConsoleLogLine>();
    }

    // 1. REQ-06: 실제 OS의 현재 가용(남은) RAM 용량(GB) 실시간 측정
    public double GetHostAvailableRamGb()
    {
        try
        {
            // OCI Ubuntu(리눅스) 환경일 때: /proc/meminfo에서 실제 MemAvailable 읽기
            if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
            {
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemAvailable:"))
                    {
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (long.TryParse(parts[1], out long kb))
                            return Math.Round(kb / (1024.0 * 1024.0), 1); // KB -> GB 변환
                    }
                }
            }
        }
        catch { }

        // 윈도우(개발 PC) 환경일 때: .NET GC 메모리 정보 활용
        var info = GC.GetGCMemoryInfo();
        double totalGb = info.TotalAvailableMemoryBytes / (1024.0 * 1024.0 * 1024.0);
        return Math.Round(totalGb, 1);
    }

    // 2. 서버 시작 (가용 RAM 체크 + 슬롯별 .jar 독립 복사 + 포트 고정 + 감사 로그 기록)
    public async Task<(bool Success, string Message)> StartServerAsync(int instanceId, string actorUsername)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var instance = await db.ServerInstances.FindAsync(instanceId);
        if (instance == null)
            return (false, "서버 인스턴스를 찾을 수 없습니다.");

        var proc = GetOrCreateProcess(instanceId);
        if (proc.IsAlive)
            return (false, "이미 실행 중인 서버입니다.");

        // REQ-06: 호스트 가용 메모리 초과 방어 (최소 1.5GB는 OS 여유분으로 남김)
        double availableGb = GetHostAvailableRamGb();
        if (instance.AllocatedRamGb > availableGb - 1.5)
        {
            return (false, $"호스트 가용 메모리가 부족합니다! (요청: {instance.AllocatedRamGb}GB, 현재 여유: {availableGb}GB)");
        }

        // REQ-04: 공용 보관소에 버킷(.jar) 파일이 있는지 확인
        string templateJarPath = Path.Combine(JarsPath, instance.JarFileName);
        if (!File.Exists(templateJarPath))
            return (false, $"버킷 파일({instance.JarFileName})이 보관소에 없습니다.");

        // REQ-01: 독립 작업 폴더 준비 및 server.properties 포트 강제 고정
        Directory.CreateDirectory(instance.WorkingDirectory);
        await EnsureServerPortAsync(instance.WorkingDirectory, instance.Port);

        // 다중 슬롯에서 동일한 버킷(.jar)을 동시에 실행할 때 파일 잠금(Lock) 및 I/O 병목을 막기 위해 슬롯 폴더로 복사
        string localJarPath = Path.Combine(instance.WorkingDirectory, instance.JarFileName);
        if (!File.Exists(localJarPath) || new FileInfo(localJarPath).Length != new FileInfo(templateJarPath).Length)
        {
            File.Copy(templateJarPath, localJarPath, overwrite: true);
        }

        // REQ-05: Java 버전(8, 17, 21, 25)에 맞는 실행 경로 찾기
        string javaExec = ResolveJavaPath(instance.JavaVersion);

        bool started = proc.Start(javaExec, localJarPath, instance.AllocatedRamGb, instance.WorkingDirectory);
        if (!started)
            return (false, "자바 프로세스 실행에 실패했습니다.");

        // 누가 서버를 켰는지 AuditLog 장부에 박제
        db.AuditLogs.Add(new AuditLog
        {
            Username = actorUsername,
            Action = "서버 시작",
            Target = $"슬롯 {instance.SlotNumber} ({instance.Name}, {instance.AllocatedRamGb}GB)"
        });
        await db.SaveChangesAsync();

        return (true, $"'{instance.Name}' 서버 부팅을 시작했습니다.");
    }

    // 3. 서버 안전 종료 (REQ-02 + 감사 로그 기록)
    public async Task<(bool Success, string Message)> StopServerAsync(int instanceId, string actorUsername)
    {
        if (!_processes.TryGetValue(instanceId, out var proc) || !proc.IsAlive)
            return (false, "현재 실행 중인 서버가 아닙니다.");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var instance = await db.ServerInstances.FindAsync(instanceId);

        db.AuditLogs.Add(new AuditLog
        {
            Username = actorUsername,
            Action = "서버 종료",
            Target = instance != null ? $"슬롯 {instance.SlotNumber} ({instance.Name})" : $"ID {instanceId}"
        });
        await db.SaveChangesAsync();

        await proc.StopGracefullyAsync();
        return (true, "서버가 안전하게 저장 및 종료되었습니다.");
    }

    // 웹 서버(MCWS) 자체가 종료될 때 켜져 있는 모든 마인크래프트 서버를 안전하게 저장 후 종료 (고아 프로세스 방지)
    public async Task ShutdownAllAsync()
    {
        var runningProcs = _processes.Values.Where(p => p.IsAlive).ToList();
        if (runningProcs.Count == 0) return;

        Console.WriteLine($"[MCWS] 웹 서버 종료 감지: 실행 중인 마인크래프트 서버 {runningProcs.Count}개를 안전 종료(stop)합니다...");
        var stopTasks = runningProcs.Select(p => p.StopGracefullyAsync(timeoutSeconds: 60));
        await Task.WhenAll(stopTasks);
        Console.WriteLine("[MCWS] 모든 마인크래프트 서버가 안전하게 종료되었습니다.");
    }

    // 4. 명령어 전송 (REQ-12)
    public async Task<bool> SendCommandAsync(int instanceId, string command)
    {
        if (!_processes.TryGetValue(instanceId, out var proc) || !proc.IsAlive)
            return false;

        return await proc.SendCommandAsync(command);
    }

    // 5. REQ-03: 인스턴스 삭제 (실행 중일 때는 원천 차단 + 디스크 슬롯 폴더까지 완전 초기화!)
    public async Task<(bool Success, string Message)> DeleteInstanceAsync(int instanceId, string actorUsername)
    {
        if (GetStatus(instanceId) != ServerStatus.Offline)
            return (false, "실행 중이거나 부팅/종료 중인 서버는 삭제할 수 없습니다! 먼저 완전히 종료해주세요.");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var instance = await db.ServerInstances.FindAsync(instanceId);
        if (instance == null)
            return (false, "삭제할 서버를 찾을 수 없습니다.");

        // 슬롯 삭제 시 기존 월드 및 설정 파일이 남지 않도록 슬롯 폴더(WorkingDirectory)도 함께 삭제
        if (Directory.Exists(instance.WorkingDirectory))
        {
            try
            {
                Directory.Delete(instance.WorkingDirectory, recursive: true);
            }
            catch { }
        }

        db.ServerInstances.Remove(instance);
        db.AuditLogs.Add(new AuditLog
        {
            Username = actorUsername,
            Action = "서버 인스턴스 삭제",
            Target = $"슬롯 {instance.SlotNumber} ({instance.Name})"
        });
        await db.SaveChangesAsync();

        _processes.TryRemove(instanceId, out _);
        return (true, $"슬롯 {instance.SlotNumber} 서버와 기존 월드 데이터가 모두 초기화(삭제)되었습니다.");
    }

    // 5-2. 월드 초기화 (서버 설정/EULA는 유지하고 world, world_nether, world_the_end만 삭제하여 새 월드 생성)
    public async Task<(bool Success, string Message)> ResetWorldAsync(int instanceId, string actorUsername)
    {
        if (GetStatus(instanceId) != ServerStatus.Offline)
            return (false, "서버가 Offline(종료) 상태일 때만 월드를 초기화할 수 있습니다.");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var instance = await db.ServerInstances.FindAsync(instanceId);
        if (instance == null)
            return (false, "서버 정보를 찾을 수 없습니다.");

        DeleteWorldFolders(instance.WorkingDirectory);

        db.AuditLogs.Add(new AuditLog
        {
            Username = actorUsername,
            Action = "월드 초기화",
            Target = $"슬롯 {instance.SlotNumber} ({instance.Name})"
        });
        await db.SaveChangesAsync();

        return (true, $"슬롯 {instance.SlotNumber}의 기존 월드가 삭제되었습니다. 서버를 시작하면 새로운 월드가 생성됩니다.");
    }

    // 6. REQ-07: 보관소의 월드 맵(.zip)을 서버의 world 폴더에 압축 해제하여 적용
    public async Task<(bool Success, string Message)> ApplyWorldMapAsync(int instanceId, int mapId, string actorUsername)
    {
        if (GetStatus(instanceId) != ServerStatus.Offline)
            return (false, "서버가 꺼져 있을 때만 월드 맵을 교체할 수 있습니다.");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var instance = await db.ServerInstances.FindAsync(instanceId);
        var map = await db.WorldMaps.FindAsync(mapId);
        if (instance == null || map == null)
            return (false, "서버 또는 맵 정보를 찾을 수 없습니다.");

        string zipPath = Path.Combine(MapsPath, map.FileName);
        if (!File.Exists(zipPath))
            return (false, $"맵 파일({map.FileName})이 디스크에 없습니다.");

        // 기존 오버월드/네더/엔더 폴더 모두 제거
        DeleteWorldFolders(instance.WorkingDirectory);

        string worldDir = Path.Combine(instance.WorkingDirectory, "world");
        Directory.CreateDirectory(worldDir);
        ZipFile.ExtractToDirectory(zipPath, worldDir);

        // 만약 .zip 파일 안에 상위 폴더가 한 겹 더 감싸져 있다면(예: world/맵이름/level.dat) 자동으로 한 단계 꺼내주기
        FlattenNestedWorldFolder(worldDir);

        db.AuditLogs.Add(new AuditLog
        {
            Username = actorUsername,
            Action = "월드 맵 적용",
            Target = $"슬롯 {instance.SlotNumber} <- {map.FileName}"
        });
        await db.SaveChangesAsync();

        return (true, $"'{map.FileName}' 맵이 슬롯 {instance.SlotNumber}에 적용되었습니다.");
    }

    // 7. REQ-08: 현재 플레이 중인 world 폴더를 압축해서 맵 보관소로 백업(Export)
    public async Task<(bool Success, string Message)> ExportWorldMapAsync(int instanceId, string customFileName, string actorUsername)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var instance = await db.ServerInstances.FindAsync(instanceId);
        if (instance == null)
            return (false, "서버를 찾을 수 없습니다.");

        // 실행 중이면 save-all flush로 메모리 데이터를 디스크에 강제 기록 후 압축
        if (_processes.TryGetValue(instanceId, out var proc) && proc.IsAlive)
        {
            await proc.SendCommandAsync("save-all flush");
            await Task.Delay(2000);
        }

        string worldDir = Path.Combine(instance.WorkingDirectory, "world");
        if (!Directory.Exists(worldDir))
            return (false, "아직 생성된 world 폴더가 없습니다. 서버를 한 번 이상 실행해주세요.");

        string safeName = string.IsNullOrWhiteSpace(customFileName)
            ? $"slot{instance.SlotNumber}_backup_{DateTime.Now:yyyyMMdd_HHmmss}.zip"
            : (customFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? customFileName.Trim() : $"{customFileName.Trim()}.zip");

        string zipPath = Path.Combine(MapsPath, safeName);
        if (File.Exists(zipPath)) File.Delete(zipPath);

        ZipFile.CreateFromDirectory(worldDir, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);

        var fi = new FileInfo(zipPath);
        db.WorldMaps.Add(new WorldMap
        {
            FileName = safeName,
            FileSizeBytes = fi.Length,
            UploadedAt = DateTime.UtcNow
        });

        db.AuditLogs.Add(new AuditLog
        {
            Username = actorUsername,
            Action = "월드 맵 백업(Export)",
            Target = $"슬롯 {instance.SlotNumber} -> {safeName}"
        });
        await db.SaveChangesAsync();

        return (true, $"현재 월드가 '{safeName}' 이름으로 보관소에 백업되었습니다.");
    }

    private void DeleteWorldFolders(string workingDir)
    {
        string[] worldDirs = ["world", "world_nether", "world_the_end"];
        foreach (var dirName in worldDirs)
        {
            string fullPath = Path.Combine(workingDir, dirName);
            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
        }
    }

    private void FlattenNestedWorldFolder(string worldDir)
    {
        // world/level.dat가 바로 있으면 정상 구조
        if (File.Exists(Path.Combine(worldDir, "level.dat"))) return;

        // 하위 폴더 안에 level.dat가 있는 경우 해당 폴더의 내용물을 worldDir 루트로 이동
        var subDirs = Directory.GetDirectories(worldDir);
        foreach (var sub in subDirs)
        {
            if (File.Exists(Path.Combine(sub, "level.dat")))
            {
                foreach (var file in Directory.GetFiles(sub))
                {
                    string destFile = Path.Combine(worldDir, Path.GetFileName(file));
                    File.Move(file, destFile, overwrite: true);
                }
                foreach (var dir in Directory.GetDirectories(sub))
                {
                    string destDir = Path.Combine(worldDir, Path.GetFileName(dir));
                    if (Directory.Exists(destDir)) Directory.Delete(destDir, recursive: true);
                    Directory.Move(dir, destDir);
                }
                Directory.Delete(sub, recursive: true);
                break;
            }
        }
    }

    private ServerProcess GetOrCreateProcess(int instanceId)
    {
        return _processes.GetOrAdd(instanceId, id =>
        {
            var p = new ServerProcess(id);
            p.OnLog += (instId, line) => OnLogBroadcast?.Invoke(instId, line);
            p.OnStatusChanged += (instId, status) => OnStatusBroadcast?.Invoke(instId, status);
            return p;
        });
    }

    private string ResolveJavaPath(int version)
    {
        // 1. 리눅스/윈도우 환경변수(JAVA8_HOME, JAVA17_HOME, JAVA21_HOME, JAVA25_HOME) 우선 탐색
        string? home = Environment.GetEnvironmentVariable($"JAVA{version}_HOME");
        if (!string.IsNullOrEmpty(home))
        {
            string bin = Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
            if (File.Exists(bin)) return bin;
        }

        // 2. OCI Ubuntu (ARM64 / AMD64) 표준 OpenJDK 설치 경로 자동 탐색
        if (OperatingSystem.IsLinux())
        {
            string[] candidates =
            [
                $"/usr/lib/jvm/jdk-{version}/bin/java",
                $"/usr/lib/jvm/java-{version}-openjdk-arm64/bin/java",
                $"/usr/lib/jvm/java-{version}-openjdk-amd64/bin/java"
            ];
            foreach (var path in candidates)
            {
                if (File.Exists(path)) return path;
            }
        }

        return "java";
    }

    private async Task EnsureServerPortAsync(string workingDir, int port)
    {
        string propPath = Path.Combine(workingDir, "server.properties");
        var lines = File.Exists(propPath) ? (await File.ReadAllLinesAsync(propPath)).ToList() : new List<string>();

        int idx = lines.FindIndex(l => l.StartsWith("server-port="));
        if (idx >= 0) lines[idx] = $"server-port={port}";
        else lines.Add($"server-port={port}");

        await File.WriteAllLinesAsync(propPath, lines);
    }

    public List<string> GetInstalledAddons(string workingDirectory, AddonType type)
    {
        string subfolder = type == AddonType.Plugin ? "plugins" : "mods";
        string targetDir = Path.Combine(workingDirectory, subfolder);
        if (!Directory.Exists(targetDir)) return new List<string>();

        return Directory.GetFiles(targetDir, "*.jar")
            .Select(Path.GetFileName)
            .Where(f => !string.IsNullOrEmpty(f))
            .ToList()!;
    }

    public async Task<(bool Success, string Message)> InstallAddonToSlotAsync(int instanceId, string addonFileName, AddonType type, string actorUsername)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var instance = await db.ServerInstances.FindAsync(instanceId);
        if (instance == null) return (false, "서버 인스턴스를 찾을 수 없습니다.");

        string sourceFolder = type == AddonType.Plugin ? PluginsPath : ModsPath;
        string sourceFile = Path.Combine(sourceFolder, addonFileName);
        if (!File.Exists(sourceFile)) return (false, "보관소에 해당 파일이 존재하지 않습니다.");

        string targetFolder = Path.Combine(instance.WorkingDirectory, type == AddonType.Plugin ? "plugins" : "mods");
        Directory.CreateDirectory(targetFolder);

        string targetFile = Path.Combine(targetFolder, addonFileName);
        File.Copy(sourceFile, targetFile, overwrite: true);

        db.AuditLogs.Add(new AuditLog
        {
            Username = actorUsername,
            Action = $"{(type == AddonType.Plugin ? "플러그인" : "모드")} 장착",
            Target = $"슬롯 {instance.SlotNumber} <- {addonFileName}"
        });
        await db.SaveChangesAsync();

        return (true, $"'{addonFileName}'이(가) 슬롯 {instance.SlotNumber}에 장착되었습니다.");
    }

    public async Task<(bool Success, string Message)> RemoveAddonFromSlotAsync(int instanceId, string addonFileName, AddonType type, string actorUsername)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var instance = await db.ServerInstances.FindAsync(instanceId);
        if (instance == null) return (false, "서버 인스터스를 찾을 수 없습니다.");

        string targetFolder = Path.Combine(instance.WorkingDirectory, type == AddonType.Plugin ? "plugins" : "mods");
        string targetFile = Path.Combine(targetFolder, addonFileName);

        if (File.Exists(targetFile))
        {
            File.Delete(targetFile);
        }

        db.AuditLogs.Add(new AuditLog
        {
           Username = actorUsername,
           Action = $"{(type == AddonType.Plugin ? "플러그인" : "모드")} 제거",
           Target = $"슬롯 {instance.SlotNumber}에서 {addonFileName} 삭제" 
        });
        await db.SaveChangesAsync();

        return (true, $"'{addonFileName}'이(가) 슬롯 {instance.SlotNumber}에서 제거되었습니다.");
    }
}
