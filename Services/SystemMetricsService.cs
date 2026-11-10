using System.Diagnostics;

namespace MCWS_v2.Services;

// 잠깐 측정값을 담아 전달하고 스택에서 즉시 소멸하는 읽기 전용 구조체
public readonly record struct RamMetrics(
    double TotalRamGb,        // 호스트 전체 물리 램 (예: 24.0 GB)
    double SystemUsedRamGb,   // OCI OS + 웹서버 순수 점유 램 (예: 1.2 GB)
    double ReservedMcRamGb,   // 가동 중인 마크 서버들의 예약(-Xmx) 램 합계 (예: 8.0 GB)
    double SafeAvailableRamGb // 새로 할당 가능한 안전 잔여 램 (예: 14.8 GB)
);

public class SystemMetricsService
{
    /// <summary>
    /// 현재 호스트의 램 상태를 정밀 계산합니다.
    /// (전체 물리 램 - 순수 OS/웹서버 점유 램 - 가동 중인 마크 서버들의 예약 램 합계)
    /// </summary>
    /// <param name="runningJavaProcesses">현재 실행 중인 마크 서버의 OS Process 객체 목록</param>
    /// <param name="reservedMcRamGbSum">현재 가동 중인 마크 서버들의 설정된 할당 램(AllocatedRamGb) 총합</param>
    public RamMetrics GetRamMetrics(IEnumerable<Process> runningJavaProcesses, int reservedMcRamGbSum)
    {
        var (totalBytes, osAvailableBytes) = ReadHostMemoryBytes();

        // 1. 현재 OS 전체에서 사용 중인 램 (Bytes)
        long totalOsUsedBytes = Math.Max(0, totalBytes - osAvailableBytes);

        // 2. 실행 중인 마크 Java 프로세스들이 실제로 점유 중인 물리 램(WorkingSet64 = RSS) 합산
        long actualMcUsedBytes = 0;
        foreach (var proc in runningJavaProcesses)
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Refresh(); // 최신 상태 갱신
                    actualMcUsedBytes += proc.WorkingSet64;
                }
            }
            catch
            {
                // 프로세스가 방금 종료되었거나 접근 불가한 경우 무시
            }
        }

        // 3. 순수 시스템(OCI OS + 웹서버) 점유 램 = 전체 사용 램 - 마크 실제 점유 램
        long systemUsedBytes = Math.Max(0, totalOsUsedBytes - actualMcUsedBytes);

        // 바이트(Bytes) -> 기가바이트(GB) 변환 (소수점 첫째 자리 반올림)
        double totalGb = Math.Round(totalBytes / (1024.0 * 1024.0 * 1024.0), 1);
        double systemUsedGb = Math.Round(systemUsedBytes / (1024.0 * 1024.0 * 1024.0), 1);
        double reservedMcGb = reservedMcRamGbSum;

        // 4. 최종 안전 가용 램 = 전체 램 - 순수 시스템 램 - 마크 예약 램(-Xmx)
        double safeAvailableGb = Math.Max(0, Math.Round(totalGb - systemUsedGb - reservedMcGb, 1));

        return new RamMetrics(totalGb, systemUsedGb, reservedMcGb, safeAvailableGb);
    }

    // OS별로 전체 물리 램(Total)과 현재 여유 램(Available)을 바이트 단위로 읽어오는 내부 함수
    private (long TotalBytes, long AvailableBytes) ReadHostMemoryBytes()
    {
        // 리눅스(OCI Ubuntu) 환경: /proc/meminfo 파싱
        if (OperatingSystem.IsLinux() && File.Exists("/proc/meminfo"))
        {
            long totalKb = 0;
            long availableKb = 0;

            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:"))
                    totalKb = ParseMeminfoKb(line);
                else if (line.StartsWith("MemAvailable:"))
                    availableKb = ParseMeminfoKb(line);

                if (totalKb > 0 && availableKb > 0)
                    break;
            }

            if (totalKb > 0)
                return (totalKb * 1024L, availableKb * 1024L);
        }

        // 윈도우(개발 PC) 환경: .NET GC 메모리 정보 활용
        var gcInfo = GC.GetGCMemoryInfo();
        long winTotal = gcInfo.TotalAvailableMemoryBytes;
        long winUsed = gcInfo.MemoryLoadBytes; // 현재 OS 전체 메모리 사용량
        long winAvailable = Math.Max(0, winTotal - winUsed);

        return (winTotal, winAvailable);
    }

    private long ParseMeminfoKb(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], out long kb) ? kb : 0;
    }
}
