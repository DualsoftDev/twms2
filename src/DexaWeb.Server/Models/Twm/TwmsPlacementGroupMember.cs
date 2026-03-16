namespace DexaWeb.Server.Models.Twm;

/// <summary>
/// 배치 그룹의 멤버 자산. GroupId + AssetId 복합키.
/// </summary>
public class TwmsPlacementGroupMember
{
    public int GroupId { get; set; }
    public int AssetId { get; set; }
}
