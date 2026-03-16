namespace DexaWeb.Server.HOCON;

/// <summary>
/// Parameter field data type.
/// DEXA 파라미터 항목의 UI 렌더링 및 유효성 검사용 타입.
/// </summary>
public enum DataType
{
    String,
    File,
    Directory,
    DateTime,
    IPAddress,
    /// <summary>Combo selection (candidates 필요)</summary>
    Selection,
    Int,        // legacy — Numeric 으로 매핑
    Numeric,
    Password,
    Memo,
    Text,
    /// <summary>Drive type 의 +/- 버튼 (DEXA UI 전용)</summary>
    Button,
}
