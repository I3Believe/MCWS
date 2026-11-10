using System.IO.Compression;
using System.Text.Json;
using MCWS_v2.Models;

namespace MCWS_v2.Services;

// 월드 백업 시 함께 저장할 애드온 목록 데이터 구조
public class WorldAddonManifest
{
    public List<string> Plugins { get; set; } = new();
    public List<string> Mods { get; set; } = new();
}

public class StorageService
{
    private const string ManifestFileName = "mcws_addons.json";
    private const int MaxSearchDepth = 3; // level.dat 탐색 최대 깊이 (무한 루프 방지)

    // 보관소 및 인스턴스 기본 폴더 경로
    public string BaseStoragePath => Path.Combine(Directory.GetCurrentDirectory(), "storage");
    public string JarsPath => Path.Combine(BaseStoragePath, "templates", "jars");
    public string MapsPath => Path.Combine(BaseStoragePath, "templates", "maps");
    public string PluginsPath => Path.Combine(BaseStoragePath, "templates", "plugins");
    public string ModsPath => Path.Combine(BaseStoragePath, "templates", "mods");
    public string InstancesPath => Path.Combine(BaseStoragePath, "instances");

    public StorageService()
    {
        Directory.CreateDirectory(JarsPath);
        Directory.CreateDirectory(MapsPath);
        Directory.CreateDirectory(PluginsPath);
        Directory.CreateDirectory(ModsPath);
        Directory.CreateDirectory(InstancesPath);
    }

    // 1. 애드온 종류에 따른 보관소 폴더 경로 반환
    public string GetTemplateDirectory(AddonType type) => type switch
    {
        AddonType.Plugin => PluginsPath,
        AddonType.Mod => ModsPath,
        AddonType.World => MapsPath,
        _ => BaseStoragePath
    };

    // 2. 슬롯의 server.properties에 고유 포트 강제 고정
    public async Task EnsureServerPortAsync(string workingDir, int port)
    {
        Directory.CreateDirectory(workingDir);
        string propPath = Path.Combine(workingDir, "server.properties");
        var lines = File.Exists(propPath)
            ? (await File.ReadAllLinesAsync(propPath)).ToList()
            : new List<string>();

        int idx = lines.FindIndex(l => l.StartsWith("server-port="));
        if (idx >= 0)
            lines[idx] = $"server-port={port}";
        else
            lines.Add($"server-port={port}");

        await File.WriteAllLinesAsync(propPath, lines);
    }

    // 3. 설정 파일(eula.txt, server.properties) 읽기 및 저장 (UI에서 분리)
    public async Task<string> ReadConfigFileAsync(string workingDir, string fileName)
    {
        string path = Path.Combine(workingDir, fileName);
        return File.Exists(path) ? await File.ReadAllTextAsync(path) : string.Empty;
    }

    public async Task WriteConfigFileAsync(string workingDir, string fileName, string content)
    {
        Directory.CreateDirectory(workingDir);
        string path = Path.Combine(workingDir, fileName);
        await File.WriteAllTextAsync(path, content);
    }

    // 4. 슬롯의 기존 월드 폴더(world, world_nether, world_the_end) 초기화 삭제
    public void DeleteWorldFolders(string workingDir)
    {
        string[] worldDirs = ["world", "world_nether", "world_the_end"];
        foreach (var dirName in worldDirs)
        {
            string fullPath = Path.Combine(workingDir, dirName);
            if (Directory.Exists(fullPath))
                Directory.Delete(fullPath, recursive: true);
        }
    }

    // 5. 슬롯에 현재 설치된 플러그인 또는 모드 파일명 목록 조회
    public List<string> GetInstalledAddons(string workingDir, AddonType type)
    {
        if (type == AddonType.World) return new List<string>();

        string folderName = type == AddonType.Plugin ? "plugins" : "mods";
        string targetDir = Path.Combine(workingDir, folderName);

        if (!Directory.Exists(targetDir)) return new List<string>();

        return Directory.GetFiles(targetDir)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Cast<string>()
            .OrderBy(name => name)
            .ToList();
    }

    // 6. 플러그인/모드 1개를 보관소에서 슬롯으로 복사
    public bool CopyAddonToSlot(string workingDir, string fileName, AddonType type)
    {
        string sourcePath = Path.Combine(GetTemplateDirectory(type), fileName);
        if (!File.Exists(sourcePath)) return false;

        string folderName = type == AddonType.Plugin ? "plugins" : "mods";
        string targetDir = Path.Combine(workingDir, folderName);
        Directory.CreateDirectory(targetDir);

        string destPath = Path.Combine(targetDir, fileName);
        File.Copy(sourcePath, destPath, overwrite: true);
        return true;
    }

    // 7. 슬롯에서 플러그인/모드 1개 삭제
    public bool RemoveAddonFromSlot(string workingDir, string fileName, AddonType type)
    {
        string folderName = type == AddonType.Plugin ? "plugins" : "mods";
        string targetPath = Path.Combine(workingDir, folderName, fileName);

        if (!File.Exists(targetPath)) return false;

        File.Delete(targetPath);
        return true;
    }

    // 8. 현재 슬롯의 월드를 애드온 정보(mcws_addons.json)와 함께 Zip으로 백업
    public async Task<(bool Success, long FileSizeBytes)> ExportWorldWithAddonsAsync(string workingDir, string destZipPath)
    {
        string worldDir = Path.Combine(workingDir, "world");
        if (!Directory.Exists(worldDir)) return (false, 0);

        // 현재 슬롯에 깔린 플러그인과 모드 목록을 JSON으로 월드 폴더 안에 기록
        var manifest = new WorldAddonManifest
        {
            Plugins = GetInstalledAddons(workingDir, AddonType.Plugin),
            Mods = GetInstalledAddons(workingDir, AddonType.Mod)
        };

        string manifestPath = Path.Combine(worldDir, ManifestFileName);
        string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(manifestPath, json);

        if (File.Exists(destZipPath)) File.Delete(destZipPath);
        ZipFile.CreateFromDirectory(worldDir, destZipPath, CompressionLevel.Optimal, includeBaseDirectory: false);

        return (true, new FileInfo(destZipPath).Length);
    }

    // 9. 월드 Zip 적용 (3단계 평탄화 + 원본 템플릿 재압축 + 보관소에 있는 애드온만 자동 세팅 도우미)
    public async Task<(bool Success, ResultCode Code)> ApplyWorldZipAsync(string workingDir, string zipFileName)
    {
        string zipPath = Path.Combine(MapsPath, zipFileName);
        if (!File.Exists(zipPath))
            return (false, ResultCode.NotFoundFile);

        DeleteWorldFolders(workingDir);

        string worldDir = Path.Combine(workingDir, "world");
        Directory.CreateDirectory(worldDir);
        ZipFile.ExtractToDirectory(zipPath, worldDir, overwriteFiles: true);

        // 최대 3단계 깊이까지만 level.dat 위치 탐색
        string? actualWorldRoot = FindLevelDatDirectory(worldDir, currentDepth: 0);
        if (actualWorldRoot == null)
        {
            DeleteWorldFolders(workingDir);
            return (false, ResultCode.InvalidWorldStructure);
        }

        // 만약 하위 폴더에 중첩되어 있었다면 루트(worldDir)로 끌어올리고 원본 템플릿 Zip도 재압축(정규화)
        if (!string.Equals(actualWorldRoot, worldDir, StringComparison.OrdinalIgnoreCase))
        {
            FlattenToRoot(actualWorldRoot, worldDir);

            // 원본 템플릿 Zip을 정규화된 루트 구조로 재압축하여 덮어쓰기 (.tmp 임시 파일로 ACID 보장)
            string tempZipPath = $"{zipPath}.tmp";
            if (File.Exists(tempZipPath)) File.Delete(tempZipPath);
            ZipFile.CreateFromDirectory(worldDir, tempZipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            File.Move(tempZipPath, zipPath, overwrite: true);
        }

        // mcws_addons.json이 있다면 보관소에 존재하는 애드온만 초기 세팅으로 자동 복사 (없어도 강제 중단하지 않음)
        bool hasMissingAddon = false;
        string manifestPath = Path.Combine(worldDir, ManifestFileName);
        if (File.Exists(manifestPath))
        {
            string json = await File.ReadAllTextAsync(manifestPath);
            var manifest = JsonSerializer.Deserialize<WorldAddonManifest>(json);

            if (manifest != null)
            {
                foreach (var mod in manifest.Mods)
                {
                    if (!CopyAddonToSlot(workingDir, mod, AddonType.Mod))
                        hasMissingAddon = true;
                }
                foreach (var plugin in manifest.Plugins)
                {
                    if (!CopyAddonToSlot(workingDir, plugin, AddonType.Plugin))
                        hasMissingAddon = true;
                }
            }
        }

        return (true, hasMissingAddon ? ResultCode.SuccessWithMissingAddons : ResultCode.Success);
    }

    // 재귀 탐색: 최대 3단계(MaxSearchDepth)까지만 level.dat가 있는 폴더를 찾음
    private string? FindLevelDatDirectory(string currentDir, int currentDepth)
    {
        if (File.Exists(Path.Combine(currentDir, "level.dat")))
            return currentDir;

        if (currentDepth >= MaxSearchDepth)
            return null;

        foreach (var subDir in Directory.GetDirectories(currentDir))
        {
            var found = FindLevelDatDirectory(subDir, currentDepth + 1);
            if (found != null)
                return found;
        }

        return null;
    }

    // 중첩된 하위 폴더(sourceDir)의 내용물을 루트(rootDir)로 이동시키고 남은 빈 껍데기 폴더 정리
    private void FlattenToRoot(string sourceDir, string rootDir)
    {
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            string destFile = Path.Combine(rootDir, Path.GetFileName(file));
            File.Move(file, destFile, overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            string destDir = Path.Combine(rootDir, Path.GetFileName(dir));
            if (Directory.Exists(destDir))
                Directory.Delete(destDir, recursive: true);
            Directory.Move(dir, destDir);
        }

        // 원래 루트 바로 아래에 있던 껍데기 중첩 폴더 삭제
        foreach (var topSubDir in Directory.GetDirectories(rootDir))
        {
            if (sourceDir.StartsWith(topSubDir, StringComparison.OrdinalIgnoreCase))
            {
                Directory.Delete(topSubDir, recursive: true);
                break;
            }
        }
    }
}
