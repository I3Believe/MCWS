namespace MCWS_v2.Models;

// 버킷의 역할 정의
public enum ServerLoaderType
{
    PluginOnly,
    ModOnly,
    Hybrid
}

// 추가 파일 종류
public enum AddonType
{
    Plugin,
    Mod,
    World
}

// 1. 공용 버킷(.jar) 보관소 테이블
public class ServerTemplate
{
    public int Id { get; set; } // 기본키

    public string FileName { get; set; } = string.Empty; // 실제 .jar 파일명 (예: "paper-1.21.1.jar")

    public int RecommendedJavaVersion { get; set; } = 21; // 실행할 Java 버전 (8, 17, 21)

    public ServerLoaderType LoaderType { get; set; } = ServerLoaderType.PluginOnly; // 서버 로더 유형 (PluginOnly, ModOnly, Hybrid)

    public long FileSizeBytes { get; set; } // 파일 용량 (바이트)

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow; // 업로드 시각
}

// 공용 월드 플러그인 모드 보관소 테이블
public class AddonFile
{
    public int Id { get; set; } // 기본키

    public string FileName { get; set; } = string.Empty; // 실제 디스크 파일명 (이름 변경 시 디스크 파일명도 함께 변경)

    public AddonType Type { get; set; } = AddonType.Plugin; // 추가 파일 종류 (Plugin, Mod)

    public long FileSizeBytes { get; set; } // 파일 용량 (바이트)

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow; // 업로드 시각
}