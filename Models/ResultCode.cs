namespace MCWS_v2.Models;

public enum ResultCode
{   
    // 0-999 성공 (계정: 0~9 / 서버: 10~19 / 파일 및 애드온: 20~29)
    FirstUser = 0, // 최초 가입
    SuccessRegist = 1, // 가입 성공
    SuccessLogin = 2, // 로그인 성공
    SuccessApproved = 3, // 가입 승인 성공
    SuccessRejected = 4, // 가입 거절 성공

    Success = 10, // 일반 작업 성공
    SuccessServerCreated = 11, // 서버 슬롯 생성 성공
    SuccessServerStarted = 12, // 서버 시작 성공
    SuccessServerStopped = 13, // 서버 안전 종료 성공
    SuccessServerDeleted = 14, // 서버 슬롯 삭제 성공
    SuccessConfigSaved = 15, // 설정 파일(eula.txt / server.properties) 저장 성공

    SuccessFileUploaded = 20, // 보관소 파일 업로드(또는 갱신) 성공
    SuccessFileDeleted = 21, // 보관소 파일 삭제 성공
    SuccessAddonInstalled = 22, // 슬롯에 애드온(월드/플러그인/모드) 적용 성공
    SuccessWithMissingAddons = 23, // 월드 적용 성공 (일부 애드온이 보관소에 없어 제외됨)
    SuccessAddonRemoved = 24, // 슬롯에서 애드온 제거(또는 월드 초기화) 성공
    SuccessWorldExported = 25, // 슬롯 월드 보관소 백업(Export) 성공

    // 1000: 계정 관련
    NotInput = 1000, // 아이디 또는 비밀번호 미입력
    equalInput = 1001, // 사용중인 아이디
    NotLogin = 1002, // 로그인 실패
    NotApproved = 1003, // 승인 대기 중인 계정
    NotPermission = 1004, // 권한이 없음
    NotUser = 1005, // 존재하지 않는 사용자

    // 2000: 서버 인스턴스 및 자원 관련
    NotFoundInstance = 2000, // 서버 슬롯을 찾을 수 없음
    ServerNotOffline = 2001, // 서버가 꺼져 있을 때만 가능한 작업
    ServerAlreadyRunning = 2002, // 이미 실행 중인 서버
    NotEnoughRam = 2003, // 가용 램 부족
    ProcessStartFailed = 2004, // 자바 프로세스 실행 실패
    InvalidSlotInput = 2005, // 서버 이름 미입력 또는 버킷 템플릿 미선택
    DuplicateSlotOrName = 2006, // 이미 사용 중인 슬롯 번호 또는 서버 이름

    // 3000: 파일 및 애드온(월드/플러그인/모드) 관련
    NotFoundFile = 3000, // 파일을 찾을 수 없음
    InvalidWorldStructure = 3001, // 3단계 내에 level.dat가 없는 잘못된 월드 파일
    InvalidFileName = 3002, // 올바르지 않은 파일명
    WorldFolderNotFound = 3003, // 아직 생성된 월드(world 폴더)가 없어 백업 불가
}
