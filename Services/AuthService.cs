using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MCWS.Data;
using MCWS.Models;

namespace MCWS.Services;

public class AuthService
{
    private readonly AppDbContext _db;
    // .NET 내장 표준 PBKDF2 단방향 암호화 도구
    private readonly PasswordHasher<User> _hasher = new();

    public AuthService(AppDbContext db)
    {
        _db = db;
    }

    // 1. 회원가입 신청 (최초 가입자는 자동으로 Master 승격 및 즉시 승인)
    public async Task<(bool Success, string Message)> RegisterAsync(string username, string password)
    {
        username = username.Trim();
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            return (false, "아이디와 비밀번호를 입력해주세요.");

        if (await _db.Users.AnyAsync(u => u.Username.ToLower() == username.ToLower()))
            return (false, "이미 사용 중인 아이디입니다.");

        // DB에 회원이 0명이면 이 사람이 '최초 가입자(Master)'
        bool isFirstUser = !await _db.Users.AnyAsync();

        var user = new User
        {
            Username = username,
            Role = isFirstUser ? UserRole.Master : UserRole.Operator,
            IsApproved = isFirstUser, // 최초 가입자는 즉시 true, 나머지는 false(대기)
            ApprovedBy = isFirstUser ? "SYSTEM" : null
        };

        // 비밀번호를 PBKDF2 방식으로 해싱해서 저장
        user.PasswordHash = _hasher.HashPassword(user, password);

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        if (isFirstUser)
            return (true, "최초 가입자이므로 Master(최고 관리자)로 즉시 등록되었습니다.");

        return (true, "가입 신청이 완료되었습니다. 관리자 승인 후 로그인할 수 있습니다.");
    }

    // 2. 로그인 검증
    public async Task<(bool Success, string Message, User? User)> ValidateLoginAsync(string username, string password)
    {
        username = username.Trim();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower());

        if (user == null)
            return (false, "아이디 또는 비밀번호가 일치하지 않습니다.", null);

        // 입력한 비밀번호와 DB의 해시값 비교
        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
            return (false, "아이디 또는 비밀번호가 일치하지 않습니다.", null);

        if (!user.IsApproved)
            return (false, "관리자 승인 대기 중인 계정입니다.", null);

        return (true, "로그인 성공", user);
    }

    // 3. 가입 승인 또는 거절 (Master와 Admin 모두 가능, 거절 시 DB에서 즉시 삭제)
    public async Task<(bool Success, string Message)> ProcessApprovalAsync(int targetUserId, bool approve, User actor)
    {
        if (actor.Role != UserRole.Master && actor.Role != UserRole.Admin)
            return (false, "가입 승인 권한이 없습니다.");

        var target = await _db.Users.FindAsync(targetUserId);
        if (target == null)
            return (false, "대상 계정을 찾을 수 없습니다.");

        if (approve)
        {
            target.IsApproved = true;
            target.ApprovedBy = actor.Username; // 누가 승인했는지 기록!
            _db.AuditLogs.Add(new AuditLog
            {
                Username = actor.Username,
                Action = "가입 승인",
                Target = $"유저 {target.Username}"
            });
            await _db.SaveChangesAsync();
            return (true, $"'{target.Username}' 계정을 승인했습니다.");
        }
        else
        {
            // 방식 A: 거절 시 DB에서 즉시 삭제
            _db.Users.Remove(target);
            _db.AuditLogs.Add(new AuditLog
            {
                Username = actor.Username,
                Action = "가입 거절(삭제)",
                Target = $"유저 {target.Username}"
            });
            await _db.SaveChangesAsync();
            return (true, $"'{target.Username}' 가입 요청을 거절하고 삭제했습니다.");
        }
    }

    // 4. 이미 승인된 기존 계정 삭제 및 권한 변경 (오직 최초 관리자인 Master만 가능!)
    public async Task<(bool Success, string Message)> DeleteApprovedUserAsync(int targetUserId, User actor)
    {
        if (actor.Role != UserRole.Master)
            return (false, "승인된 계정의 삭제는 Master(최초 관리자)만 가능합니다.");

        var target = await _db.Users.FindAsync(targetUserId);
        if (target == null)
            return (false, "대상 계정을 찾을 수 없습니다.");

        if (target.Role == UserRole.Master)
            return (false, "Master 계정은 삭제할 수 없습니다.");

        _db.Users.Remove(target);
        _db.AuditLogs.Add(new AuditLog
        {
            Username = actor.Username,
            Action = "기존 계정 강제 삭제",
            Target = $"유저 {target.Username} (승인자: {target.ApprovedBy})"
        });
        await _db.SaveChangesAsync();

        return (true, $"'{target.Username}' 계정이 영구 삭제되었습니다.");
    }

    // 5. Master가 일반 유저를 Admin(부관리자)으로 임명/해제
    public async Task<(bool Success, string Message)> ChangeUserRoleAsync(int targetUserId, UserRole newRole, User actor)
    {
        if (actor.Role != UserRole.Master)
            return (false, "관리자 권한 부여는 Master만 가능합니다.");

        var target = await _db.Users.FindAsync(targetUserId);
        if (target == null || target.Role == UserRole.Master)
            return (false, "대상을 변경할 수 없습니다.");

        target.Role = newRole;
        _db.AuditLogs.Add(new AuditLog
        {
            Username = actor.Username,
            Action = $"권한 변경 ({newRole})",
            Target = $"유저 {target.Username}"
        });
        await _db.SaveChangesAsync();

        return (true, $"'{target.Username}'의 권한을 {newRole}(으)로 변경했습니다.");
    }
}