namespace DEX.Core.Actor

open System.Reactive.Subjects
open Akka.Actor
open Akka.Remote
open Microsoft.Extensions.Logging

/// DEXA 서버로부터 알림을 수신하는 클라이언트 ShortTerm actor.
/// 서버의 DEXServerShortTermActor가 보내는 메시지를 처리.
type DexaClientShortTermActor(logger: ILogger) =
    inherit DexaBaseActor(logger)

    static member val DataChanged: Subject<DataChangedNotification> = new Subject<DataChangedNotification>() with get
    static member val MessageSubject: Subject<obj> = new Subject<obj>() with get

    override this.OnReceive(message: obj) =
        let sender = this.Sender

        logger.LogInformation("ShortTerm actor 메시지 수신: {Type} from {Sender}", message.GetType().Name :> obj, sender.Path.ToString() :> obj)
        DexaClientShortTermActor.MessageSubject.OnNext(message)

        match message with
        | :? AmReplySubscription as sub ->
            let s2c = sub :?> AmS2CReplySubscription
            if not (isNull s2c) then
                logger.LogInformation(
                    "DEXA Server 구독 응답 수신 (serverVersion={Version})",
                    (if isNull s2c.ServerVersion then "?" else s2c.ServerVersion) :> obj)
            else
                logger.LogInformation("DEXA Server 구독 응답 수신 (base type)")
            // 구독 성공 → 연결 확인 알림
            DexaClientShortTermActor.DataChanged.OnNext(ConnectivityChangedNotification(true))

        | :? AmS2CNotifyDataChanged as xnotify ->
            if not (isNull xnotify.DataChanges) then
                for dc in xnotify.DataChanges do
                    DexaClientShortTermActor.DataChanged.OnNext(dc)

        | :? AmS2CServerLockSteteChanged as lockChanged ->
            DexaClientShortTermActor.DataChanged.OnNext(ServerLockStatusChangedNotification(lockChanged.Locker))

        | :? AmPing as query ->
            sender.Tell(AmPong(query))

        | :? AmS2CReminderMessage as reminderMsg ->
            DexaClientShortTermActor.DataChanged.OnNext(ReminderStatusChangeNotification(reminderMsg.Data))

        | :? AmS2CRefreshMessage ->
            DexaClientShortTermActor.DataChanged.OnNext(RefreshNotification())

        | :? AssociationErrorEvent as associationError ->
            logger.LogError("Quarantined by {RemoteAddress}", associationError.RemoteAddress :> obj)
            DexaActorSystem.ActorSystemSubject.OnNext(DEX.Common.ActorErrors.Quarantined())

        | :? Terminated as t ->
            logger.LogWarning("서버 actor Terminated 수신: {Actor}", t.ActorRef.Path.ToString() :> obj)
            DexaClientShortTermActor.DataChanged.OnNext(ConnectivityChangedNotification(false))

        | _ ->
            logger.LogDebug("처리되지 않은 메시지: {Type} - {Message}", message.GetType().Name :> obj, message :> obj)

    override this.PreStart() =
        base.PreStart()
        let ctx = UntypedActor.Context
        ctx.System.EventStream.Subscribe(this.Self, typeof<AssociationErrorEvent>) |> ignore
        // 서버 ShortTerm actor를 Watch → Terminated 수신으로 연결 끊김 감지
        if not (isNull DexaActorSystem.ServerShortTermActor) then
            ctx.Watch(DexaActorSystem.ServerShortTermActor) |> ignore
