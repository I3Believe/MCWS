using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.EntityFrameworkCore;
using MCWS.Data;
using MCWS.Models;

namespace MCWS.Services;

public class AuthStateService
{
    // 브라우저 탭/창이 열려 있는 동안(F5 새로고침 포함)만 유지되고, 창을 닫으면 즉시 파기되는 세션 저장소
    private readonly ProtectedSessionStorage _storage;
    private readonly IServiceScopeFactory _scopeFactory;

    // 현재 로그인된 사용자 정보
    public User? CurrentUser { get; private set; }

    // 로그인 및 승인 완료 여부 확인
    public bool IsLoggedIn => CurrentUser != null && CurrentUser.IsApproved;

    // 권한별 빠른 확인 프로퍼티
    public bool IsMaster => IsLoggedIn && CurrentUser?.Role == UserRole.Master;
    public bool IsAdminOrMaster => IsLoggedIn && (CurrentUser?.Role == UserRole.Master || CurrentUser?.Role == UserRole.Admin);

    // 로그인/로그아웃 시 화면(UI)을 즉시 새로고침하기 위한 알림 이벤트
    public event Action? OnChange;

    public AuthStateService(ProtectedSessionStorage storage, IServiceScopeFactory scopeFactory)
    {
        _storage = storage;
        _scopeFactory = scopeFactory;
    }

    // 1. 로그인 성공 시 현재 브라우저 세션 암호화 금고에 유저 ID 저장 (창 닫으면 자동 삭제)
    public async Task LoginAsync(User user)
    {
        CurrentUser = user;
        await _storage.SetAsync("mcws_auth_uid", user.Id);
        OnChange?.Invoke();
    }

    // 2. 브라우저 새로고침(F5) 시 세션 금고에서 유저 복원
    public async Task RestoreSessionAsync()
    {
        if (CurrentUser != null) return;

        try
        {
            var result = await _storage.GetAsync<int>("mcws_auth_uid");
            if (result.Success)
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var user = await db.Users.FirstOrDefaultAsync(u => u.Id == result.Value && u.IsApproved);
                if (user != null)
                {
                    CurrentUser = user;
                    OnChange?.Invoke();
                }
            }
        }
        catch
        {
            // 저장소 토큰이 만료되었거나 변조된 경우 무시
        }
    }

    // 3. 로그아웃 시 금고 비우기
    public async Task LogoutAsync()
    {
        CurrentUser = null;
        await _storage.DeleteAsync("mcws_auth_uid");
        OnChange?.Invoke();
    }
}
