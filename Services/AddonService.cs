using Microsoft.EntityFrameworkCore;
using MCWS_v2.Data;
using MCWS_v2.Models;

namespace MCWS_v2.Services;

public class AddonService
{
    private readonly AppDbContext _db;
    private readonly StorageService _storage;
    private readonly ServerInstanceManager _instanceManager;

    public AddonService(AppDbContext db, StorageService storage, ServerInstanceManager instanceManager)
    {
        _db = db;
        _storage = storage;
        _instanceManager = instanceManager;
    }

    // 0. 보관소 화면용 목록 조회 (버킷 템플릿 및 월드/플러그인/모드 목록)
    public async Task<List<ServerTemplate>> GetServerTemplatesAsync() =>
        await _db.ServerTemplates.OrderByDescending(t => t.UploadedAt).ToListAsync();

    public async Task<List<AddonFile>> GetAddonFilesAsync(AddonType? filterType = null)
    {
        var query = _db.AddonFiles.AsQueryable();
        if (filterType != null)
            query = query.Where(a => a.Type == filterType.Value);

        return await query.OrderByDescending(a => a.UploadedAt).ToListAsync();
    }

    // 1. 공용 버킷(.jar) 보관소 업로드 (중복 시 갱신 + .tmp 임시 파일 및 2단계 트랜잭션 ACID 보장)
    public async Task<(bool Success, ResultCode Code)> UploadServerTemplateAsync(
        string fileName,
        Stream fileStream,
        int javaVersion,
        ServerLoaderType loaderType,
        string actorUsername)
    {
        string safeName = Path.GetFileName(fileName.Trim());
        if (string.IsNullOrEmpty(safeName) || !safeName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            return (false, ResultCode.InvalidFileName);

        string finalPath = Path.Combine(_storage.JarsPath, safeName);
        string tempPath = $"{finalPath}.tmp"; // ACID 보장을 위한 임시 파일

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            // 1단계: 기존 원본 파일을 바로 건드리지 않고 임시 파일(.tmp)로 먼저 저장
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                await fileStream.CopyToAsync(fs);
            }

            long fileSize = new FileInfo(tempPath).Length;

            // 2단계: DB에서 같은 이름의 버킷이 있는지 조회 (FirstOrDefaultAsync로 조회와 수정을 한 번에 처리)
            var existing = await _db.ServerTemplates.FirstOrDefaultAsync(
                t => t.FileName.ToLower() == safeName.ToLower());

            if (existing != null)
            {
                // 이미 있으면 새로 Add하지 않고 설정과 용량, 시각만 갱신
                existing.RecommendedJavaVersion = javaVersion;
                existing.LoaderType = loaderType;
                existing.FileSizeBytes = fileSize;
                existing.UploadedAt = DateTime.UtcNow;
            }
            else
            {
                // 없으면 신규 등록
                _db.ServerTemplates.Add(new ServerTemplate
                {
                    FileName = safeName,
                    RecommendedJavaVersion = javaVersion,
                    LoaderType = loaderType,
                    FileSizeBytes = fileSize,
                    UploadedAt = DateTime.UtcNow
                });
            }

            _db.AuditLogs.Add(new AuditLog
            {
                Username = actorUsername,
                Action = "버킷 템플릿 업로드",
                Target = $"{safeName} (Java {javaVersion}, {loaderType})"
            });

            // 3단계: DB 변경사항 준비 후 임시 파일(.tmp)을 실제 파일로 교체하고 트랜잭션 최종 확정(Commit)
            await _db.SaveChangesAsync();
            File.Move(tempPath, finalPath, overwrite: true);
            await tx.CommitAsync();

            return (true, ResultCode.SuccessFileUploaded);
        }
        catch
        {
            // 중간에 에러 발생 시 DB 롤백 및 임시 파일만 깨끗이 지우면 기존 파일과 DB 모두 100% 원상복구됨
            await tx.RollbackAsync();
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
    }

    // 2. 공용 버킷(.jar) 보관소 삭제 (임시 백업 후 DB 확정 시 삭제)
    public async Task<(bool Success, ResultCode Code)> DeleteServerTemplateAsync(int templateId, string actorUsername)
    {
        var template = await _db.ServerTemplates.FindAsync(templateId);
        if (template == null)
            return (false, ResultCode.NotFoundFile);

        string path = Path.Combine(_storage.JarsPath, template.FileName);
        string trashPath = $"{path}.del";

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            if (File.Exists(path))
                File.Move(path, trashPath, overwrite: true);

            _db.ServerTemplates.Remove(template);
            _db.AuditLogs.Add(new AuditLog
            {
                Username = actorUsername,
                Action = "버킷 템플릿 삭제",
                Target = template.FileName
            });
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            if (File.Exists(trashPath))
                File.Delete(trashPath);

            return (true, ResultCode.SuccessFileDeleted);
        }
        catch
        {
            await tx.RollbackAsync();
            if (File.Exists(trashPath) && !File.Exists(path))
                File.Move(trashPath, path, overwrite: true);
            throw;
        }
    }

    // 3. 공용 애드온(월드 .zip / 플러그인 .jar / 모드 .jar) 보관소 업로드 (중복 시 갱신 + ACID 보장)
    public async Task<(bool Success, ResultCode Code)> UploadAddonFileAsync(
        string fileName,
        Stream fileStream,
        AddonType type,
        string actorUsername)
    {
        string safeName = Path.GetFileName(fileName.Trim());
        string requiredExt = type == AddonType.World ? ".zip" : ".jar";
        if (string.IsNullOrEmpty(safeName) || !safeName.EndsWith(requiredExt, StringComparison.OrdinalIgnoreCase))
            return (false, ResultCode.InvalidFileName);

        string folderPath = _storage.GetTemplateDirectory(type);
        string finalPath = Path.Combine(folderPath, safeName);
        string tempPath = $"{finalPath}.tmp"; // ACID 보장을 위한 임시 파일

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            // 1단계: 임시 파일(.tmp)로 먼저 디스크에 기록
            await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
            {
                await fileStream.CopyToAsync(fs);
            }

            long fileSize = new FileInfo(tempPath).Length;

            // 2단계: 동일 파일명이 있으면 용량/시각만 갱신, 없으면 신규 추가
            var existing = await _db.AddonFiles.FirstOrDefaultAsync(
                a => a.Type == type && a.FileName.ToLower() == safeName.ToLower());

            if (existing != null)
            {
                existing.FileSizeBytes = fileSize;
                existing.UploadedAt = DateTime.UtcNow;
            }
            else
            {
                _db.AddonFiles.Add(new AddonFile
                {
                    FileName = safeName,
                    Type = type,
                    FileSizeBytes = fileSize,
                    UploadedAt = DateTime.UtcNow
                });
            }

            _db.AuditLogs.Add(new AuditLog
            {
                Username = actorUsername,
                Action = $"보관소 업로드 ({type})",
                Target = safeName
            });

            // 3단계: DB 변경사항 준비 후 임시 파일을 실제 파일로 교체하고 트랜잭션 최종 확정
            await _db.SaveChangesAsync();
            File.Move(tempPath, finalPath, overwrite: true);
            await tx.CommitAsync();

            return (true, ResultCode.SuccessFileUploaded);
        }
        catch
        {
            await tx.RollbackAsync();
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
    }

    // 4. 공용 애드온 보관소 삭제 (임시 백업 후 DB 확정 시 삭제)
    public async Task<(bool Success, ResultCode Code)> DeleteAddonFileAsync(int addonId, string actorUsername)
    {
        var addon = await _db.AddonFiles.FindAsync(addonId);
        if (addon == null)
            return (false, ResultCode.NotFoundFile);

        string path = Path.Combine(_storage.GetTemplateDirectory(addon.Type), addon.FileName);
        string trashPath = $"{path}.del";

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            if (File.Exists(path))
                File.Move(path, trashPath, overwrite: true);

            _db.AddonFiles.Remove(addon);
            _db.AuditLogs.Add(new AuditLog
            {
                Username = actorUsername,
                Action = $"보관소 삭제 ({addon.Type})",
                Target = addon.FileName
            });
            await _db.SaveChangesAsync();
            await tx.CommitAsync();

            if (File.Exists(trashPath))
                File.Delete(trashPath);

            return (true, ResultCode.SuccessFileDeleted);
        }
        catch
        {
            await tx.RollbackAsync();
            if (File.Exists(trashPath) && !File.Exists(path))
                File.Move(trashPath, path, overwrite: true);
            throw;
        }
    }

    // 5. 슬롯에 애드온(플러그인 / 모드 / 월드) 통합 적용
    public async Task<(bool Success, ResultCode Code)> InstallAddonToSlotAsync(int instanceId, int addonId, string actorUsername)
    {
        var instance = await _db.ServerInstances.FindAsync(instanceId);
        if (instance == null)
            return (false, ResultCode.NotFoundInstance);

        // ServerInstanceManager(메모리)에 직접 물어봐서 서버가 실제로 꺼져 있는지 확인
        if (_instanceManager.GetStatus(instanceId) != ServerStatus.Offline)
            return (false, ResultCode.ServerNotOffline);

        var addon = await _db.AddonFiles.FindAsync(addonId);
        if (addon == null)
            return (false, ResultCode.NotFoundFile);

        ResultCode resultCode = ResultCode.SuccessAddonInstalled;

        if (addon.Type == AddonType.World)
        {
            // 월드 맵 적용 (3단계 평탄화 + 원본 정규화 + 보관소 내 애드온 초기 세팅)
            var (success, code) = await _storage.ApplyWorldZipAsync(instance.WorkingDirectory, addon.FileName);
            if (!success)
                return (false, code);

            resultCode = code == ResultCode.Success ? ResultCode.SuccessAddonInstalled : code;
        }
        else
        {
            // 플러그인 또는 모드 단일 파일 복사
            bool copied = _storage.CopyAddonToSlot(instance.WorkingDirectory, addon.FileName, addon.Type);
            if (!copied)
                return (false, ResultCode.NotFoundFile);
        }

        _db.AuditLogs.Add(new AuditLog
        {
            Username = actorUsername,
            Action = $"애드온 적용 ({addon.Type})",
            Target = $"슬롯 {instance.SlotNumber} <- {addon.FileName}"
        });
        await _db.SaveChangesAsync();

        return (true, resultCode);
    }

    // 6. 슬롯에서 애드온 제거 (플러그인/모드는 파일 삭제, 월드는 월드 폴더 초기화)
    public async Task<(bool Success, ResultCode Code)> RemoveAddonFromSlotAsync(int instanceId, string fileName, AddonType type, string actorUsername)
    {
        var instance = await _db.ServerInstances.FindAsync(instanceId);
        if (instance == null)
            return (false, ResultCode.NotFoundInstance);

        // ServerInstanceManager(메모리)에 직접 물어봐서 서버가 실제로 꺼져 있는지 확인
        if (_instanceManager.GetStatus(instanceId) != ServerStatus.Offline)
            return (false, ResultCode.ServerNotOffline);

        if (type == AddonType.World)
        {
            _storage.DeleteWorldFolders(instance.WorkingDirectory);
        }
        else
        {
            bool removed = _storage.RemoveAddonFromSlot(instance.WorkingDirectory, fileName, type);
            if (!removed)
                return (false, ResultCode.NotFoundFile);
        }

        _db.AuditLogs.Add(new AuditLog
        {
            Username = actorUsername,
            Action = $"애드온 제거 ({type})",
            Target = $"슬롯 {instance.SlotNumber} (제거: {fileName})"
        });
        await _db.SaveChangesAsync();

        return (true, ResultCode.SuccessAddonRemoved);
    }

    // 7. 현재 슬롯의 월드를 보관소(AddonFiles)로 백업 (중복 이름 시 갱신 + 파일/DB ACID 보장)
    public async Task<(bool Success, ResultCode Code)> ExportWorldToAddonAsync(int instanceId, string customFileName, string actorUsername)
    {
        var instance = await _db.ServerInstances.FindAsync(instanceId);
        if (instance == null)
            return (false, ResultCode.NotFoundInstance);

        // 실행 중에는 파일 잠금(Lock) 충돌이 발생하므로 Offline 상태에서만 월드 백업 허용
        if (_instanceManager.GetStatus(instanceId) != ServerStatus.Offline)
            return (false, ResultCode.ServerNotOffline);

        string safeName = string.IsNullOrWhiteSpace(customFileName)
            ? $"slot{instance.SlotNumber}_backup_{DateTime.Now:yyyyMMdd_HHmmss}.zip"
            : (customFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileName(customFileName.Trim())
                : Path.GetFileName($"{customFileName.Trim()}.zip"));

        string finalZipPath = Path.Combine(_storage.MapsPath, safeName);
        string tempZipPath = $"{finalZipPath}.tmp"; // ACID 보장을 위한 임시 압축 파일

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            // 1단계: 기존 원본 파일을 바로 건드리지 않고 임시 파일(.tmp)로 먼저 압축 생성
            var (exported, fileSize) = await _storage.ExportWorldWithAddonsAsync(instance.WorkingDirectory, tempZipPath);
            if (!exported)
                return (false, ResultCode.WorldFolderNotFound);

            // 2단계: DB에서 같은 이름의 월드 애드온이 있는지 조회 (FirstOrDefaultAsync로 조회와 수정을 한 번에 처리)
            var existingAddon = await _db.AddonFiles.FirstOrDefaultAsync(
                a => a.Type == AddonType.World && a.FileName.ToLower() == safeName.ToLower());

            if (existingAddon != null)
            {
                // 이미 있으면 새로 Add하지 않고 용량과 시각만 갱신
                existingAddon.FileSizeBytes = fileSize;
                existingAddon.UploadedAt = DateTime.UtcNow;
            }
            else
            {
                // 없으면 신규 등록
                _db.AddonFiles.Add(new AddonFile
                {
                    FileName = safeName,
                    Type = AddonType.World,
                    FileSizeBytes = fileSize,
                    UploadedAt = DateTime.UtcNow
                });
            }

            _db.AuditLogs.Add(new AuditLog
            {
                Username = actorUsername,
                Action = "월드 백업(Export)",
                Target = $"슬롯 {instance.SlotNumber} -> {safeName}"
            });

            // 3단계: DB 변경사항 준비 후 임시 파일(.tmp)을 실제 파일로 교체하고 트랜잭션 최종 확정(Commit)
            await _db.SaveChangesAsync();
            File.Move(tempZipPath, finalZipPath, overwrite: true);
            await tx.CommitAsync();

            return (true, ResultCode.SuccessWorldExported);
        }
        catch
        {
            // 중간에 에러 발생 시 DB 롤백 및 임시 파일만 깨끗이 지우면 기존 파일과 DB 모두 100% 원상복구됨
            await tx.RollbackAsync();
            if (File.Exists(tempZipPath))
                File.Delete(tempZipPath);

            throw;
        }
    }
}
