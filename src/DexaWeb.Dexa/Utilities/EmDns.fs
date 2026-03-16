namespace DEX.Common

open System.Net
open System.Net.NetworkInformation
open System.Net.Sockets

module EmDns =
    let getLocalIpAddress () =
        if not (NetworkInterface.GetIsNetworkAvailable()) then
            IPAddress.Loopback
        else
            Dns.GetHostEntry(Dns.GetHostName()).AddressList
            |> Seq.tryFind (fun ip -> ip.AddressFamily = AddressFamily.InterNetwork)
            |> Option.defaultValue IPAddress.Loopback
