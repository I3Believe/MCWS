using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using MCWS_v2.Data;
using MCWS_v2.Models;

namespace MCWS_v2.Services;

public class ServerInstanceManager
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly StorageService _storage;
    private readonly SystemMetricsService _metrics;
    private readonly ConcurrentDictionary<int, ServerProcess> _processes = new();

    // 웹 화면으로 실시간 로그와 상태를 쏴주는 이벤트
    public event Action<int, ConsoleLogLine>? OnLogBroadcast;
    public event Action<int, ServerStatus>? OnStatusBroadcast;

    public ServerInstanceManager(
        IServiceScopeFactory scopeFactory,
        StorageService storage,
        SystemMetricsService metrics)
    {
        _scopeFactory = scopeFactory;
        _storage = storage;
        _metrics = metrics;
    }

    public ServerStatus GetStatus(int instanceId) =>
        _processes.TryGetValue(instanceId, out var proc) ? proc.Status : ServerStatus.Offline;

    public IReadOnlyList<ConsoleLogLine> GetLogs(int instanceId) =>
        _processes.TryGetValue(instanceId, out var proc) ? proc.GetRecentLogs() : Array.Empty<ConsoleLogLine>();

    // 0. 대시보드 화면용 슬롯 목록 조회 및 신규 슬롯 생성 (화면에서 DB/폴더 직접 접근 차단)
    public async Task<List<ServerInstance>> GetServerInstancesAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var list = await db.ServerInstances.OrderBy(s => s.SlotNumber).ToListAsync();

        // 현재 실행 중인 프로세스 상태를 인스턴스 객체에 동기화
        foreach (var inst in list)
        {
            inst.Status = GetStatus(inst.Id);
        }
        return list;
    }

    public async Task<(bool Success, ResultCode Code)> CreateServerInstanceAsync(
        int slotNumber,
        string name,
        int allocatedRamGb,
        int templateId,
        string actorUsername)
    {
        if (string.IsNullOrWhiteSpace(name) || slotNumber < 1 || slotNumber > 3 || templateId <= 0)
            return (false, ResultCode.InvalidSlotInput);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // 이미 해당 슬롯 번호나 이름이 사용 중인지 확인
        bool exists = await db.ServerInstances.AnyAsync(
            s => s.SlotNumber == slotNumber || s.Name.ToLower() == name.Trim().ToLower());
        if (exists)
            return (false, ResultCode.DuplicateSlotOrName);

        var template = await db.ServerTemplates.FindAsync(templateId);
        if (template == null)
            return (false, ResultCode.NotFoundFile);

        int port = 25564 + slotNumber; // 슬롯 1 -> 25565, 슬롯 2 -> 25566, 슬롯 3 -> 25567
        string workingDir = Path.Combine(_storage.InstancesPath, $"slot-{slotNumber}");
        Directory.CreateDirectory(workingDir);

        await _storage.EnsureServerPortAsync(workingDir, port);

        var instance = new ServerInstance
        {
            SlotNumber = slotNumber,
            Port = port,
            Name = name.Trim(),
            AllocatedRamGb = allocatedRamGb,
            JarFileName = template.FileName,
            LoaderType = template.LoaderType,
            JavaVersion = template.RecommendedJavaVersion,
            WorkingDirectory = workingDir,
            CreatedBy = actorUsername,
            Status = ServerStatus.Offline,
            CreatedAt = DateTime.UtcNow
        };

        db.ServerInstances.Add(instance);
        db.AuditLogs.Add(new AuditLog
        {
            Username = actorUsername,
            Action = "서버 슬롯 생성",
            Target = $"슬롯 {slotNumber} ({instance.Name}, Port: {port}, {allocatedRamGb}GB)"
        });
        await db.SaveChangesAsync();

        return (true, ResultCode.SuccessServerCreated);
    }

    // 1. 현재 실행 중인 마크 서버들의 예약 램(-Xmx)을 반영한 정밀 램 상태 조회
    public RamMetrics GetCurrentRamMetrics()
    {
        var aliveProcs = _processes.Values.Where(p => p.IsAlive).ToList();

        var rawOsProcs = aliveProcs
            .Select(p => p.RawProcess)
            .Where(p => p != null)
            .Cast<Process>();

        int reservedRamGbSum = aliveProcs.Sum(p => p.AllocatedRamGb);

        return _metrics.GetRamMetrics(rawOsProcs, reservedRamGbSum);
    }

    // 2. 서버 시작 (안전 가용 램 체크 + 포트 고정 + 슬롯별 .jar 복사 + 프로세스 실행)
    public async Task<(bool Success, ResultCode Code)> StartServerAsync(int instanceId, string actorUsername)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var instance = await db.ServerInstances.FindAsync(instanceId);
        if (instance == null)
            return (false, ResultCode.NotFoundInstance);

        var proc = GetOrCreateProcess(instanceId);
        if (proc.IsAlive)
            return (false, ResultCode.ServerAlreadyRunning);

        // 예약 기반 안전 가용 램 확인
        var ramMetrics = GetCurrentRamMetrics();
        if (instance.AllocatedRamGb > ramMetrics.SafeAvailableRamGb)
            return (false, ResultCode.NotEnoughRam);

        // 공용 보관소에 버킷(.jar) 파일이 있는지 확인
        string templateJarPath = Path.Combine(_storage.JarsPath, instance.JarFileName);
        if (!File.Exists(templateJarPath))
            return (false, ResultCode.NotFoundFile);

        // 독립 작업 폴더 준비 및 server.properties 포트 강제 고정
        await _storage.EnsureServerPortAsync(instance.WorkingDirectory, instance.Port);

        // 다중 슬롯 동시 실행 시 파일 잠금 방지를 위해 슬롯 폴더로 .jar 복사
        string localJarPath = Path.Combine(instance.WorkingDirectory, instance.JarFileName);
        if (!File.Exists(localJarPath) || new FileInfo(localJarPath).Length != new FileInfo(templateJarPath).Length)
        {
            File.Copy(templateJarPath, localJarPath, overwrite: true);
        }

        string javaExec = ResolveJavaPath(instance.JavaVersion);
        bool started = proc.Start(javaExec, localJarPath, instance.AllocatedRamGb, instance.WorkingDirectory);
        if (!started)
            return (false, ResultCode.ProcessStartFailed);

        db.AuditLogs.Add(new AuditLog
        {
            Username = actorUsername,
            Action = "서버 시작",
            Target = $"슬롯 {instance.SlotNumber} ({instance.Name}, {instance.AllocatedRamGb}GB)"
        });
        await db.SaveChangesAsync();

        return (true, ResultCode.SuccessServerStarted);
    }

    // 3. 서버 안전 종료 (stop 명령어 주입 후 대기)
    public async Task<(bool Success, ResultCode Code)> StopServerAsync(int instanceId, string actorUsername)
    {
        if (!_processes.TryGetValue(instanceId, out var proc) || !proc.IsAlive)
            return (false, ResultCode.ServerNotOffline);

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
        return (true, ResultCode.SuccessServerStopped);
    }

    // 4. 웹 서버(MCWS) 종료 시 켜져 있는 모든 마크 서버 일괄 안전 종료
    public async Task ShutdownAllAsync()
    {
        var runningProcs = _processes.Values.Where(p => p.IsAlive).ToList();
        if (runningProcs.Count == 0) return;

        var stopTasks = runningProcs.Select(p => p.StopGracefullyAsync(timeoutSeconds: 60));
        await Task.WhenAll(stopTasks);
    }

    // 5. 콘솔 명령어 전송
    public async Task<bool> SendCommandAsync(int instanceId, string command)
    {
        if (!_processes.TryGetValue(instanceId, out var proc) || !proc.IsAlive)
            return false;

        return await proc.SendCommandAsync(command);
    }

    // 6. 인스턴스 삭제 (꺼져 있을 때만 허용 + 슬롯 폴더 및 DB 삭제)
    public async Task<(bool Success, ResultCode Code)> DeleteInstanceAsync(int instanceId, string actorUsername)
    {
        if (GetStatus(instanceId) != ServerStatus.Offline)
            return (false, ResultCode.ServerNotOffline);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var instance = await db.ServerInstances.FindAsync(instanceId);
        if (instance == null)
            return (false, ResultCode.NotFoundInstance);

        if (Directory.Exists(instance.WorkingDirectory))
        {
            Directory.Delete(instance.WorkingDirectory, recursive: true);
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
        return (true, ResultCode.SuccessServerDeleted);
    }

    // 7. 슬롯 설정 파일(eula.txt / server.properties) 저장 및 감사 로그 기록
    public async Task<(bool Success, ResultCode Code)> SaveServerConfigAsync(
        int instanceId,
        string fileName,
        string content,
        string actorUsername)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var instance = await db.ServerInstances.FindAsync(instanceId);
        if (instance == null)
            return (false, ResultCode.NotFoundInstance);

        await _storage.WriteConfigFileAsync(instance.WorkingDirectory, fileName, content);

        db.AuditLogs.Add(new AuditLog
        {
            Username = actorUsername,
            Action = $"설정 파일 저장 ({fileName})",
            Target = $"슬롯 {instance.SlotNumber} ({instance.Name})"
        });
        await db.SaveChangesAsync();

        return (true, ResultCode.SuccessConfigSaved);
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
        string? home = Environment.GetEnvironmentVariable($"JAVA{version}_HOME");
        if (!string.IsNullOrEmpty(home))
        {
            string bin = Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
            if (File.Exists(bin)) return bin;
        }

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
}
