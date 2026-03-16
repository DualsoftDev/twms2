namespace DexaWeb.Dexa

open System
open System.Threading.Tasks

type IDexaClient =
    inherit IDisposable
    abstract IsConnected: bool
    abstract PingServerAsync: unit -> Task<bool>
    abstract InitializeAsync: unit -> Task
    abstract AskServerAsync<'T> : message: obj * ?timeout: TimeSpan -> Task<'T>
    abstract TellServer: message: obj -> unit
    abstract ServerNotifications: IObservable<obj>
