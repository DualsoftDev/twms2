namespace DexaWeb.Dexa.Infrastructure

// DexaJsonSerializer는 더 이상 사용하지 않음.
// DEXA Server는 Akka 기본 NewtonSoftJsonSerializer를 사용하며,
// DexaAssemblyResolver가 DEXA 어셈블리 → DexaWeb.Dexa 어셈블리로 리다이렉트하여
// 타입 해석을 처리함.
// 이 파일은 빈 모듈로 유지 (fsproj Compile 순서 의존성).

module internal DexaJsonSerializerPlaceholder =
    let _ = ()
