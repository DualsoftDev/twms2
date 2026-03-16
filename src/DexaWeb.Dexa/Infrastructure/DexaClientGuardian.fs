namespace DEX.Core.Actor

open System
open System.Reactive.Subjects
open Akka.Actor
open Akka.Remote
open Microsoft.Extensions.Logging

type DexaClientGuardian(dexaActorSystem: DexaActorSystem) =
    inherit DexaGuardianActor(
        dexaActorSystem.ClientLogger,
        DexaActorSystem.TheServerActor,
        dexaActorSystem.ServerActorPath,
        System.Action<IActorRef>(fun svr -> DexaActorSystem.TheServerActor <- svr))

    static member val DataChanged: Subject<DataChangedNotification> = new Subject<DataChangedNotification>() with get
    static member val TheGuardian: IActorRef = null with get, set
    static member val MessageSubject: Subject<obj> = new Subject<obj>() with get
    static member val ReplySubscription: AmS2CReplySubscription = null with get, set

    member private this.GoWaitSubscribed(message: obj) =
        this.ProcServerConnectivity(message) |> ignore
        match message with
        | :? AmS2CReplySubscription as sub ->
            this.Logger.LogInformation("Client got subscription reply: {Message}", message)
            DexaClientGuardian.ReplySubscription <- sub
            this.UnbecomeStacked()
        | _ ->
            this.Logger.LogDebug("Ignoring message {MessageType} until subscribed", message.GetType().Name :> obj)

    override this.OnReceive(message: obj) =
        let sender = this.Sender

        match message with
        | :? Terminated -> this.Logger.LogWarning("Client got terminated message")
        | _ -> ()

        this.Logger.LogDebug("Client got message: {MessageType}", message.GetType().Name :> obj)
        DexaClientGuardian.MessageSubject.OnNext(message)

        if not (this.ProcServerConnectivity(message)) then
            match message with
            | :? string as str when str = "show peers" ->
                let peers =
                    if isNull DexaActorSystem.TheServerActor then Unchecked.defaultof<AmS2CReplyConnectedPeers>
                    else DexaActorSystem.TheServerActor.Ask<AmS2CReplyConnectedPeers>(AmC2SRequestConnectedPeers()).Result
                this.Logger.LogInformation("Connected peers: {Peers}", peers :> obj)

            | :? AmS2CReplyConnectedPeers -> ()

            | :? AmS2XNotifyDataChanged as xnotify ->
                if not (isNull xnotify.DataChanges) then
                    for dc in xnotify.DataChanges do
                        match dc with
                        | :? DatabaseChangedNotification ->
                            this.Logger.LogDebug("Got database change notification: {Change}", dc :> obj)
                        | _ -> ()
                        DexaClientGuardian.DataChanged.OnNext(dc)

            | :? AmS2CServerLockSteteChanged as lockChanged ->
                DexaClientGuardian.DataChanged.OnNext(ServerLockStatusChangedNotification(lockChanged.Locker))

            | :? AmPing as query ->
                sender.Tell(AmPong(query))

            | :? AssociationErrorEvent as associationError ->
                this.Logger.LogError("Quarantined by {RemoteAddress}", associationError.RemoteAddress :> obj)
                DexaActorSystem.ActorSystemSubject.OnNext(DEX.Common.ActorErrors.Quarantined())

            | :? AmS2CReminderMessage as reminderMsg ->
                DexaClientGuardian.DataChanged.OnNext(ReminderStatusChangeNotification(reminderMsg.Data))

            | :? AmS2CRefreshMessage ->
                DexaClientGuardian.DataChanged.OnNext(RefreshNotification())

            | _ ->
                let errorMsg = sprintf "Client got undefined message of type %O: %O" (message.GetType()) message
                this.Logger.LogError("{ErrorMessage}", errorMsg :> obj)
                sender.Tell(message)

    override this.PreStart() =
        base.PreStart()
        let ctx = UntypedActor.Context
        DexaClientGuardian.TheGuardian <- this.Self
        ctx.System.EventStream.Subscribe(this.Self, typeof<AssociationErrorEvent>) |> ignore
        this.Become(fun msg -> this.GoWaitSubscribed(msg))
