using System.Collections.Concurrent;
using System.Diagnostics;
using MCWS.Models;

namespace MCWS.Services;

// 콘솔 화면에 한 줄씩 찍힐 로그 데이터 묶음
public class ConsoleLogLine
{
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Text { get; set; } = string.Empty;
    public bool IsError { get; set; }
}

public class ServerProcess
{
    private Process? _process;
    // REQ-11: 최근 300줄만 메모리에 유지하는 링 버퍼(큐)
    private readonly ConcurrentQueue<ConsoleLogLine> _logBuffer = new();
    private const int MaxLogLines = 300;

    public int InstanceId { get; }
    public ServerStatus Status { get; private set; } = ServerStatus.Offline;

    // 새 로그가 찍히거나 상태가 바뀔 때 웹 화면에 즉시 알려주는 이벤트
    public event Action<int, ConsoleLogLine>? OnLog;
    public event Action<int, ServerStatus>? OnStatusChanged;

    public ServerProcess(int instanceId)
    {
        InstanceId = instanceId;
    }

    public IReadOnlyList<ConsoleLogLine> GetRecentLogs() => _logBuffer.ToArray();

    public bool IsAlive => _process != null && !_process.HasExited;

    // 1. 자바 프로세스 실행
    public bool Start(string javaPath, string jarPath, int ramGb, string workingDir)
    {
        if (IsAlive) return false;

        try
        {
            UpdateStatus(ServerStatus.Starting);

            var startInfo = new ProcessStartInfo
            {
                FileName = javaPath,
                Arguments = $"-Xms{ramGb}G -Xmx{ramGb}G -jar \"{jarPath}\" nogui",
                WorkingDirectory = workingDir,
                RedirectStandardInput = true,  // 명령어 주입 통로 열기
                RedirectStandardOutput = true, // 일반 로그 수신 통로 열기
                RedirectStandardError = true,  // 에러 로그 수신 통로 열기
                UseShellExecute = false,
                CreateNoWindow = true          // 서버 뒷단에서 창 없이 조용히 실행
            };

            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            // 마인크래프트가 한 줄 출력할 때마다 실시간으로 낚아채기
            _process.OutputDataReceived += (sender, e) =>
            {
                if (e.Data == null) return;
                AddLog(e.Data, isError: false);

                // 마인크래프트 부팅 완료 문구 감지 시 Starting -> Running 전환!
                if (Status == ServerStatus.Starting && e.Data.Contains("Done (") && e.Data.Contains("For help"))
                {
                    UpdateStatus(ServerStatus.Running);
                }
            };

            _process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null) AddLog(e.Data, isError: true);
            };

            // 프로세스가 종료되었을 때 자동 감지
            _process.Exited += (sender, e) =>
            {
                AddLog("[MCWS] 서버 프로세스가 종료되었습니다.", isError: false);
                UpdateStatus(ServerStatus.Offline);
            };

            if (!_process.Start())
            {
                UpdateStatus(ServerStatus.Offline);
                return false;
            }

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            AddLog($"[MCWS] 프로세스 시작됨 (할당 RAM: {ramGb}GB)", isError: false);
            return true;
        }
        catch (Exception ex)
        {
            AddLog($"[MCWS 오류] 실행 실패: {ex.Message}", isError: true);
            UpdateStatus(ServerStatus.Offline);
            return false;
        }
    }

    // 2. REQ-12: 명령어 주입 (/op, say, list 등)
    public async Task<bool> SendCommandAsync(string command)
    {
        if (!IsAlive || _process == null) return false;

        try
        {
            await _process.StandardInput.WriteLineAsync(command);
            await _process.StandardInput.FlushAsync();
            AddLog($"> {command}", isError: false);
            return true;
        }
        catch (Exception ex)
        {
            AddLog($"[MCWS 오류] 명령어 전송 실패: {ex.Message}", isError: true);
            return false;
        }
    }

    // 3. REQ-02: 안전한 종료 (Graceful Shutdown - stop 주입 후 대기)
    public async Task StopGracefullyAsync(int timeoutSeconds = 120)
    {
        if (!IsAlive || _process == null)
        {
            UpdateStatus(ServerStatus.Offline);
            return;
        }

        UpdateStatus(ServerStatus.Stopping);
        AddLog("[MCWS] 안전 종료를 위해 'stop' 명령어를 주입합니다...", isError: false);

        await SendCommandAsync("stop");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            // 마인크래프트가 월드를 저장하고 스스로 꺼질 때까지 최대 120초 대기
            await _process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 20초가 지나도 안 꺼지고 먹통이면 강제 종료
            AddLog($"[MCWS 경고] {timeoutSeconds}초 동안 응답이 없어 강제 종료(Kill)합니다.", isError: true);
            _process.Kill(entireProcessTree: true);
        }

        UpdateStatus(ServerStatus.Offline);
    }

    private void AddLog(string text, bool isError)
    {
        var line = new ConsoleLogLine { Text = text, IsError = isError };
        _logBuffer.Enqueue(line);

        // 300줄 넘어가면 가장 오래된 첫 줄 버리기 (메모리 폭발 방지)
        while (_logBuffer.Count > MaxLogLines)
            _logBuffer.TryDequeue(out _);

        OnLog?.Invoke(InstanceId, line);
    }

    private void UpdateStatus(ServerStatus newStatus)
    {
        Status = newStatus;
        OnStatusChanged?.Invoke(InstanceId, newStatus);
    }
}