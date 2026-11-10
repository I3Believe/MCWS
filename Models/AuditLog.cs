namespace MCWS_v2.Models;

public class AuditLog
{
    public int Id { get; set; } // 로그 고유 번호 (기본키)
    public string Username { get; set; } = string.Empty; // 버튼을 누른 사용자 아이디
    public string Action { get; set; } = string.Empty; // 행동 종류 (예: "서버 시작", "서버 종료", "가입 승인")
    public string Target { get; set; } = string.Empty; // 대상 (예: "슬롯 1 - 야생서버", "유저 hong")
    public DateTime Timestamp { get; set; } = DateTime.UtcNow; // 행동이 일어난 시각
}