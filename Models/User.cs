namespace MCWS.Models;

public enum UserRole
{
    Master,
    Admin,
    Operator
}

public class User
{
    public int Id { get; set; } // DB 기본키 (자동 번호 증가)
    public string Username { get; set; } = string.Empty; // 로그인 아이디
    public string PasswordHash { get; set; } = string.Empty; // PBKDF2로 암호화된 비밀번호
    public UserRole Role { get; set; } = UserRole.Operator; // 권한 (기본값: Operator)
    public bool IsApproved { get; set; } = false; // 관리자 승인 여부 (기본값: false)
    public string? ApprovedBy { get; set; } // 이 계정을 승인해 준 관리자 아이디 기록
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow; // 계정 생성 시각
}