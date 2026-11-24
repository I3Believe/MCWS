# MCWS (Minecraft Web Server Manager)

웹 브라우저에서 여러 마인크래프트 서버(JVM 프로세스)를 생성·실행·종료하고, 실시간 콘솔과 서버 파일(버킷, 플러그인, 모드, 월드)을 관리할 수 있는 웹 관리 도구입니다.

---

## 1. 주요 기능

- **다중 서버 슬롯 관리**: 최대 3개의 마인크래프트 서버 슬롯(Port `25565` ~ `25567`)을 독립적으로 생성하고 실행·종료(강제 종료 및 120초 유예 종료)합니다.
- **실시간 웹 콘솔 및 리소스 모니터링**: 호스트 서버의 CPU·RAM 사용량과 각 슬롯의 실시간 콘솔 로그를 웹에서 확인하고 명령어를 전송합니다.
- **메모리 초과 실행 방지**: 호스트의 가용 메모리와 실행 중인 슬롯들의 할당 메모리(`-Xmx`) 합계를 계산하여 용량 초과 시 추가 실행을 제한합니다.
- **통합 파일 보관소**: 서버 버킷(`.jar`), 플러그인, 모드, 월드 맵(`.zip`)을 보관소에 업로드하고 원하는 슬롯에 바로 적용하거나 현재 월드를 백업합니다.
- **권한별 계정 관리**: `Master`, `Admin`, `Operator` 등급에 따라 가입 승인, 슬롯 제어, 콘솔 접근 권한을 구분하고 작업 로그를 기록합니다.

---

## 2. 사용 기술 및 요구 환경

### 기술 스택
- **Framework**: C#, .NET 9 (ASP.NET Core, Blazor Server, SignalR)
- **Database**: SQLite, Entity Framework Core
- **OS / Infra**: Windows / Ubuntu Linux (배포 시 `systemd`, `Nginx` 권장)

### 사전 요구 사항
- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (또는 런타임)
- 구동할 마인크래프트 버킷 버전에 맞는 **Java (OpenJDK 8 / 17 / 21 / 25)** 설치

---

## 3. 설치 및 사용 방법

### 1) 실행하기

#### 로컬 개발 환경 실행
```bash
git clone https://github.com/I3Believe/MCWS.git
cd MCWS
dotnet restore
dotnet run
```
실행 후 브라우저에서 `http://localhost:5000` (또는 콘솔에 표시된 포트)으로 접속합니다.

#### Linux 서버 배포용 빌드
```bash
dotnet publish -c Release -r linux-arm64 --self-contained true -o ./publish_linux
```
*(x86_64 환경인 경우 `-r linux-x64` 사용)*

---

### 2) 웹에서 사용하는 순서

1. **최초 관리자 계정 생성**
   - 서버 최초 실행 후 로그인 페이지에서 첫 번째로 회원가입하는 계정은 자동으로 최고 관리자(`Master`) 권한과 승인 상태를 부여받습니다.
   - 이후 가입하는 계정은 `Master` 또는 `Admin`이 **[관리자(Admin)]** 탭에서 승인해야 이용할 수 있습니다.
2. **보관소에 버킷 및 애드온 업로드**
   - **[보관소(Storage)]** 탭에서 마인크래프트 서버 구동용 버킷 파일(`.jar`)과 사용할 Java 버전, 로더 타입(플러그인/모드/하이브리드)을 등록합니다.
   - 필요한 플러그인(`.jar`), 모드(`.jar`), 월드 맵(`.zip`) 파일도 보관소에 업로드해 둡니다.
3. **슬롯 생성 및 서버 실행**
   - **[대시보드(Home)]** 탭의 빈 슬롯(`Slot 1 ~ 3`)에서 서버 이름, 할당할 RAM 용량, 보관소에 등록한 버킷 템플릿을 선택해 슬롯을 생성합니다.
   - 슬롯 제어 패널에서 `eula.txt` 동의 및 `server.properties` 설정을 확인하고, 보관소의 월드/플러그인/모드를 적용한 뒤 **시작** 버튼을 눌러 서버를 구동합니다.
   - 하단 실시간 웹 콘솔에서 부팅 로그를 확인하고 마인크래프트 서버 명령어를 입력할 수 있습니다.

---

## 4. 디렉터리 구조

```text
├── Program.cs                  # 앱 설정, DI 등록 및 SQLite DB 자동 생성
├── Data/
│   └── AppDbContext.cs         # EF Core DbContext
├── Models/                     # 사용자, 서버 슬롯, 보관소 파일, 감사 로그 모델
├── Services/                   # 프로세스 제어, 시스템 리소스 측정, 보관소·애드온·인증 서비스
├── Components/                 # Blazor Server UI 컴포넌트 및 페이지
└── storage/                    # (실행 시 자동 생성) app.db, 업로드 파일, 슬롯별 작업 폴더
```
