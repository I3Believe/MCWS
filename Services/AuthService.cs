using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MCWS_v2.Data;
using MCWS_v2.Models;

namespace MCWS_v2.Services;

public class AuthService
{
    private readonly AppDbContext _db;
    // .NET 내장 표준 PBKDF2 단방향 암호화 도구
    private readonly PasswordHasher<User> _hasher = new();
    // 최초 가입 동시성(Race Condition) 방어를 위한 배타적 락
    private static readonly SemaphoreSlim _registerLock = new(1, 1);

    public AuthService(AppDbContext db)
    {
        _db = db;
    }

    // 0. 관리자 화면용 유저 목록 및 감사 로그 조회 (UI의 DB 직접 접근 차단)
    public async Task<(List<User> PendingUsers, List<User> ApprovedUsers)> GetUsersForAdminAsync()
    {
        var allUsers = await _db.Users.OrderBy(u => u.CreatedAt).ToListAsync();
        var pending = allUsers.Where(u => !u.IsApproved).ToList();
        var approved = allUsers.Where(u => u.IsApproved).ToList();
        return (pending, approved);
    }

    public async Task<List<AuditLog>> GetRecentAuditLogsAsync(int limit = 50)
    {
        return await _db.AuditLogs
            .OrderByDescending(l => l.Timestamp)
            .Take(limit)
            .ToListAsync();
    }

    // 1. 회원가입 신청 (최초 가입자는 자동으로 Master 승격 및 즉시 승인)
    public async Task<(bool Success, ResultCode Code)> RegisterAsync(string username, string password)
    {
        username = username.Trim();
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password) || password.Length < 4)
            return (false, ResultCode.NotInput);

        await _registerLock.WaitAsync();
        try
        {
            if (await _db.Users.AnyAsync(u => u.Username.ToLower() == username.ToLower()))
                return (false, ResultCode.equalInput);

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
                return (true, ResultCode.FirstUser);

            return (true, ResultCode.SuccessRegist);
        }
        finally
        {
            _registerLock.Release();
        }
    }

    // 2. 로그인 검증
    public async Task<(bool Success, ResultCode Code, User? User)> ValidateLoginAsync(string username, string password)
    {
        username = username.Trim();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower());

        if (user == null)
            return (false, ResultCode.NotLogin, null);

        // 입력한 비밀번호와 DB의 해시값 비교
        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (result == PasswordVerificationResult.Failed)
            return (false, ResultCode.NotLogin, null);

        if (!user.IsApproved)
            return (false, ResultCode.NotApproved, null);

        return (true, ResultCode.SuccessLogin, user);
    }

    // 3. 가입 승인 또는 거절 (Master와 Admin 모두 가능, 거절 시 DB에서 즉시 삭제)
    public async Task<(bool Success, ResultCode Code)> ProcessApprovalAsync(int targetUserId, bool approve, User actor)
    {
        if (actor.Role != UserRole.Master && actor.Role != UserRole.Admin)
            return (false, ResultCode.NotPermission);

        var target = await _db.Users.FindAsync(targetUserId);
        if (target == null)
            return (false, ResultCode.NotUser);

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
            return (true, ResultCode.SuccessApproved);
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
            return (true, ResultCode.SuccessRejected);
        }
    }

    // 4. 이미 승인된 기존 계정 삭제 및 권한 변경 (오직 최초 관리자인 Master만 가능!)
    public async Task<(bool Success, ResultCode Code)> DeleteApprovedUserAsync(int targetUserId, User actor)
    {
        if (actor.Role != UserRole.Master)
            return (false, ResultCode.NotPermission);

        var target = await _db.Users.FindAsync(targetUserId);
        if (target == null)
            return (false, ResultCode.NotUser);

        if (target.Role == UserRole.Master)
            return (false, ResultCode.NotPermission);

        _db.Users.Remove(target);
        _db.AuditLogs.Add(new AuditLog
        {
            Username = actor.Username,
            Action = "기존 계정 강제 삭제",
            Target = $"유저 {target.Username} (승인자: {target.ApprovedBy})"
        });
        await _db.SaveChangesAsync();

        return (true, ResultCode.SuccessRejected);
    }

    // 5. Master가 일반 유저를 Admin(부관리자)으로 임명/해제
    public async Task<(bool Success, ResultCode Code)> ChangeUserRoleAsync(int targetUserId, UserRole newRole, User actor)
    {
        if (actor.Role != UserRole.Master)
            return (false, ResultCode.NotPermission);

        var target = await _db.Users.FindAsync(targetUserId);
        if (target == null || target.Role == UserRole.Master)
            return (false, ResultCode.NotPermission);

        target.Role = newRole;
        _db.AuditLogs.Add(new AuditLog
        {
            Username = actor.Username,
            Action = $"권한 변경 ({newRole})",
            Target = $"유저 {target.Username}"
        });
        await _db.SaveChangesAsync();

        return (true, ResultCode.SuccessApproved);
    }
}
