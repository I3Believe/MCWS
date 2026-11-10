using Microsoft.EntityFrameworkCore;
using MCWS_v2.Models;

namespace MCWS_v2.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    // 우리가 만든 5개 설계도를 실제 DB 테이블(DbSet)로 등록
    public DbSet<User> Users => Set<User>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ServerInstance> ServerInstances => Set<ServerInstance>();
    public DbSet<ServerTemplate> ServerTemplates => Set<ServerTemplate>();
    public DbSet<AddonFile> AddonFiles => Set<AddonFile>();

    // 테이블 생성 시 지켜야 할 중복 방지 규칙(Unique Index) 설정
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // 1. 같은 아이디(Username)로 중복 가입 불가
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Username)
            .IsUnique();

        // 2. 같은 슬롯 번호(1, 2, 3)나 같은 포트(25565~25567)로 서버 중복 생성 불가
        modelBuilder.Entity<ServerInstance>()
            .HasIndex(s => s.SlotNumber)
            .IsUnique();

        modelBuilder.Entity<ServerInstance>()
            .HasIndex(s => s.Port)
            .IsUnique();
    }
}