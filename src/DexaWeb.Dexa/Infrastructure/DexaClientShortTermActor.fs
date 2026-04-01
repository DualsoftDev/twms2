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

        logger.LogDebug("ShortTerm actor 메시지 수신: {Type}", message.GetType().Name :> obj)
        DexaClientShortTermActor.MessageSubject.OnNext(message)

        match message with
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

        | :? Terminated ->
            logger.LogWarning("서버 actor Terminated 수신")

        | _ ->
            logger.LogDebug("처리되지 않은 메시지: {Type} - {Message}", message.GetType().Name :> obj, message :> obj)

    override this.PreStart() =
        base.PreStart()
        let ctx = UntypedActor.Context
        ctx.System.EventStream.Subscribe(this.Self, typeof<AssociationErrorEvent>) |> ignore
