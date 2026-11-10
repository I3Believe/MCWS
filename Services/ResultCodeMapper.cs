using MCWS_v2.Models;

namespace MCWS_v2.Services;

public static class ResultCodeMapper
{
    // ResultCode 열거형을 화면에 표시할 한글 안내 문구로 변환하는 공용 사전
    public static string ToMessage(this ResultCode code) => code switch
    {
        // 성공 코드 (0 ~ 999)
        ResultCode.FirstUser => "최초 가입자이므로 Master(최고 관리자)로 즉시 등록되었습니다.",
        ResultCode.SuccessRegist => "가입 신청이 완료되었습니다. 관리자 승인 후 로그인할 수 있습니다.",
        ResultCode.SuccessLogin => "로그인되었습니다.",
        ResultCode.SuccessApproved => "정상적으로 승인(권한 변경) 처리되었습니다.",
        ResultCode.SuccessRejected => "계정 거절(삭제) 처리가 완료되었습니다.",

        ResultCode.Success => "작업이 성공적으로 완료되었습니다.",
        ResultCode.SuccessServerCreated => "새 서버 슬롯이 생성되었습니다.",
        ResultCode.SuccessServerStarted => "마인크래프트 서버 프로세스를 시작했습니다.",
        ResultCode.SuccessServerStopped => "서버가 안전하게 종료되었습니다.",
        ResultCode.SuccessServerDeleted => "서버 슬롯과 작업 폴더가 삭제되었습니다.",
        ResultCode.SuccessConfigSaved => "설정 파일이 저장되었습니다.",

        ResultCode.SuccessFileUploaded => "파일이 보관소에 안전하게 등록(갱신)되었습니다.",
        ResultCode.SuccessFileDeleted => "보관소에서 파일이 삭제되었습니다.",
        ResultCode.SuccessAddonInstalled => "선택한 애드온(월드/플러그인/모드)이 슬롯에 적용되었습니다.",
        ResultCode.SuccessWithMissingAddons => "월드가 적용되었습니다. (보관소에 없는 일부 애드온은 제외되었습니다)",
        ResultCode.SuccessAddonRemoved => "슬롯에서 선택한 애드온(또는 월드 폴더)이 제거되었습니다.",
        ResultCode.SuccessWorldExported => "현재 슬롯의 월드가 보관소에 백업(.zip)되었습니다.",

        // 계정 관련 오류 (1000번대)
        ResultCode.NotInput => "아이디와 비밀번호를 모두 입력해주세요.",
        ResultCode.equalInput => "이미 사용 중인 아이디입니다.",
        ResultCode.NotLogin => "아이디 또는 비밀번호가 일치하지 않습니다.",
        ResultCode.NotApproved => "아직 관리자 승인을 받지 못한 계정입니다.",
        ResultCode.NotPermission => "이 작업을 수행할 권한이 없습니다.",
        ResultCode.NotUser => "대상 사용자를 찾을 수 없습니다.",

        // 서버 및 자원 관련 오류 (2000번대)
        ResultCode.NotFoundInstance => "대상 서버 슬롯을 찾을 수 없습니다.",
        ResultCode.ServerNotOffline => "서버가 완전히 종료(Offline)된 상태에서만 가능합니다.",
        ResultCode.ServerAlreadyRunning => "이미 실행 중이거나 부팅 중인 서버입니다.",
        ResultCode.NotEnoughRam => "호스트 안전 가용 램(RAM)이 부족하여 서버를 시작할 수 없습니다.",
        ResultCode.ProcessStartFailed => "Java 프로세스 실행에 실패했습니다. Java 설치 상태를 확인해주세요.",
        ResultCode.InvalidSlotInput => "서버 이름을 입력하고 사용할 버킷 템플릿(.jar)을 선택해주세요.",
        ResultCode.DuplicateSlotOrName => "이미 사용 중인 슬롯 번호이거나 동일한 서버 이름이 존재합니다.",

        // 파일 및 애드온 관련 오류 (3000번대)
        ResultCode.NotFoundFile => "필요한 파일이 보관소 또는 디스크에 존재하지 않습니다.",
        ResultCode.InvalidWorldStructure => "압축 파일 3단계 내에 level.dat가 없어 유효한 마인크래프트 월드가 아닙니다.",
        ResultCode.InvalidFileName => "올바르지 않은 파일명입니다.",
        ResultCode.WorldFolderNotFound => "아직 생성된 월드 폴더(world)가 없어 백업할 수 없습니다. 서버를 한 번 이상 실행해주세요.",

        _ => $"알 수 없는 결과 코드입니다. ({code})"
    };
}
