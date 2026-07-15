using Twms2.Server.Models.Dexa;

namespace Twms2.Server.Helpers;

/// <summary>
/// 백업 액션(DexaAction) 상태 → API 응답 키/라벨 변환.
/// AssetsController / HistoryController 가 공유하는 단일 정의 —
/// result 키(inprogress/incomplete/failed/backedup/unchanged)는 정적 페이지 JS 와의 계약.
/// </summary>
public static class ActionResultHelper
{
    /// <summary>진행중 판정: IsInProgress 이되 미완료(IncompleteThreshold 초과)는 제외.</summary>
    public static bool IsInProgress(DexaAction a) => a.IsInProgress && !a.IsIncomplete;

    public static string GetResultKey(DexaAction a)
        => IsInProgress(a) ? "inprogress"
         : !a.IsSuccess ? (a.IsIncomplete ? "incomplete" : "failed")
         : a.ContentsChanged == true ? "backedup"
         : "unchanged";

    public static string GetResultLabel(DexaAction a)
        => IsInProgress(a) ? "작업중"
         : !a.IsSuccess ? (a.IsIncomplete ? "미완료" : "작업 실패")
         : a.ContentsChanged == true ? "백업 갱신"
         : "변경 없음";

    /// <summary>실패 액션에 직전 성공 버전 채움 — 단일 자산의 액션 목록용.</summary>
    public static void FillLastSuccessVersions(List<DexaAction> actions)
    {
        int? lastSuccess = null;
        foreach (var action in actions.OrderBy(a => a.Id))
        {
            if (action.IsSuccess && action.Version.HasValue)
                lastSuccess = action.Version;
            else if (!action.IsSuccess)
                action.LastSuccessVersion = lastSuccess;
        }
    }

    /// <summary>실패 액션에 직전 성공 버전 채움 — 여러 자산이 섞인 목록용(자산별로 계산).</summary>
    public static void FillLastSuccessVersionsGrouped(List<DexaAction> actions)
    {
        foreach (var group in actions.GroupBy(a => a.AssetId))
        {
            int? lastSuccess = null;
            foreach (var action in group.OrderBy(a => a.Id))
            {
                if (action.IsSuccess && action.Version.HasValue)
                    lastSuccess = action.Version;
                else if (!action.IsSuccess)
                    action.LastSuccessVersion = lastSuccess;
            }
        }
    }
}
