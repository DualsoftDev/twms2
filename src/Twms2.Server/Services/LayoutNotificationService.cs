namespace Twms2.Server.Services;

/// <summary>
/// 레이아웃 데이터 변경 알림 (PlacementEditor → LayoutView 간 연동)
/// </summary>
public class LayoutNotificationService
{
    public event Action<int>? OnLayoutChanged;

    public void NotifyLayoutChanged(int layoutId)
    {
        OnLayoutChanged?.Invoke(layoutId);
    }
}
