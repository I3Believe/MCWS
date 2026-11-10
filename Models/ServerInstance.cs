using System.ComponentModel.DataAnnotations.Schema;

namespace MCWS_v2.Models;

// 서버의 현재 실행 상태 (REQ-03 삭제 방어 및 상태 동기화용)
public enum ServerStatus
{
    Offline,    // 꺼져 있음 (삭제 및 설정 변경 가능)
    Starting,   // 부팅 중
    Running,    // 켜져 있음 (삭제 및 설정 변경 차단)
    Stopping    // stop 명령어 주입 후 안전 종료 대기 중
}

public class ServerInstance
{
    public int Id { get; set; } // 기본키

    public string CreatedBy { get; set; } = string.Empty; // 이 서버를 만든 사람 아이디

    public int SlotNumber { get; set; } // 슬롯 번호 (1, 2, 3)

    public int Port { get; set; } // 할당된 포트 (25565, 25566, 25567)

    public string Name { get; set; } = string.Empty; // 대시보드에 표시할 서버 이름

    public int AllocatedRamGb { get; set; } = 4; // 할당된 RAM 크기 (2, 4, 8, 12, 16 GB)

    public string JarFileName { get; set; } = string.Empty; // 사용할 버킷 파일명 (예: "paper-1.21.1.jar")

    public ServerLoaderType LoaderType { get; set; } = ServerLoaderType.PluginOnly; // 서버 로더 유형 (pluginOnly, ModOnly, Hybrid)
   
    public int JavaVersion { get; set; } = 25; // 실행할 Java 버전 (8, 17, 21, 25)

    public string WorkingDirectory { get; set; } = string.Empty; // 독립 작업 폴더 경로 (예: "/instances/slot-1")

    [NotMapped] // DB에 저장하지 않고 ServerInstanceManager(메모리)가 실시간으로 알려주는 상태
    public ServerStatus Status { get; set; } = ServerStatus.Offline; // 현재 상태

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow; // 생성 시각
}