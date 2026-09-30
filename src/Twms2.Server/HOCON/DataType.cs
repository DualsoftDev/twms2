namespace Twms2.Server.HOCON;

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
    /// <summary>드라이브 modelVersion (1.04 형식). 모르는 타입으로 두면 저장할 때 String 으로 바뀐다.</summary>
    Version,
    /// <summary>DEXA 가 실제로 써 놓은 값 — 현장 드라이브 13대의 modelName/modelVersion 이 이 타입이다.
    /// 의미가 있어서가 아니라 편집할 때 원래 값을 그대로 돌려주기 위해 받는다.</summary>
    Bool,
}
