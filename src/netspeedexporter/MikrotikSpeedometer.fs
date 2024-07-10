module netspeedexporter.MikrotikSpeedometer

open ByteSizeLib
open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
open System.Threading
open System.Threading.Tasks

let private serverHello : byte array =
    [| 0x01uy; 0x00uy; 0x00uy; 0x00uy |]

// Predefined client request: TCP protocol,
// direction both, using 0x00 as data, single connection
let private clientRequest : byte array =
    [
        0x01uy // TCP
        0x03uy // Direction both
        0x01uy // Use 0x00 data
        0x00uy // One connection
        0x00uy;  0x80uy // TCP constant
    ] @ (List.init 10 (fun _ -> 0x00uy))
    |> Array.ofList

let serverStatId = 0x07uy


let private resolveIpEndpoint (target: Target) (timeout: TimeSpan) : Async<IPEndPoint> =
    async {
        use token = new CancellationTokenSource (timeout)
        try
            let! addresses = Dns.GetHostAddressesAsync (target.Host, token.Token) |> Async.AwaitTask
            match addresses with
            | [|  |] -> return failwithf "DNS query returned no ip addresses for \"%s\"" target.Host
            | addrs -> return IPEndPoint (addrs[0], target.Port)
        with
        | :? OperationCanceledException ->
            return failwithf "DNS query timeout for \"%s\"" target.Host
    }


let private readExpect (stream: Stream) (expectedBytes: byte array) (name:string) : Async<unit> =
    async {
        let actualBytes: byte array = Array.zeroCreate expectedBytes.Length
        let! actualRead = stream.ReadAsync (actualBytes, 0, actualBytes.Length) |> Async.AwaitTask
        if actualRead <> expectedBytes.Length then
            failwithf "Expected \"%d\" to be read from stream, but got \"%d\" bytes." expectedBytes.Length actualRead

        if actualBytes <> expectedBytes then
            failwithf "Expected valid \"%s\" bytes from network stream" name
    }


let private parseServerStatMsg (bytes: Span<byte>) =
    match bytes.Length with
    | value when value < 12 -> failwithf "Can't parse message (id 0x07): Not enough bytes."
    | _ when bytes[0] <> serverStatId -> failwithf "Can't parse message (id 0x07): First byte is not 0x07, but %x" bytes[0]
    | _ ->
        let bytesTransfered: uint32 =
            ((uint bytes[8]) <<< 24) |||
            ((uint bytes[9]) <<< 16) |||
            ((uint bytes[10]) <<< 8) |||
            ((uint bytes[11]) <<< 0)

        ByteSize.FromBytes (float bytesTransfered)


let private startReadingPayload (stream: Stream) (token: CancellationToken) =
    async {
        let mutable totalBytesRead = 0L
        let buffer : byte array = Array.zeroCreate ((ByteSize.FromMebiBytes 1.0).Bytes |> int)

        while not token.IsCancellationRequested do
            try
                let! actualBytesRead = stream.ReadAsync (buffer, 0, buffer.Length, token) |> Async.AwaitTask
                if actualBytesRead > 0 then
                    totalBytesRead <- totalBytesRead + (int64 actualBytesRead)

            with
            | :? OperationCanceledException
            | :?  AggregateException as aggr when (aggr.InnerException :? OperationCanceledException) -> ()

        return ByteSize.FromBytes (float totalBytesRead)
    }


let private startWritingPayload (stream: Stream) (token: CancellationToken) =
    async {
        let mutable totalBytesWritten = 0L
        let buffer : byte array = Array.zeroCreate ((ByteSize.FromMebiBytes 1.0).Bytes |> int)
        while not token.IsCancellationRequested do
            try
                do! stream.WriteAsync (buffer, 0, buffer.Length, token) |> Async.AwaitTask
                totalBytesWritten <- totalBytesWritten + (int64 buffer.Length)
            with
            | :? OperationCanceledException
            | :?  AggregateException as aggr when (aggr.InnerException :? OperationCanceledException) -> ()

        return ByteSize.FromBytes (float totalBytesWritten)
    }


let measureSpeed (target: Target) (timeout: TimeSpan) : Async<NetSpeedResult> =
    async {
        let! ipEndpoint = resolveIpEndpoint target timeout
        use tcpClient = new TcpClient ()
        tcpClient.NoDelay <- true
        tcpClient.ReceiveTimeout <- int timeout.TotalMilliseconds
        tcpClient.SendTimeout <- int timeout.TotalMilliseconds

        do! tcpClient.ConnectAsync ipEndpoint |> Async.AwaitTask
        use netStream = tcpClient.GetStream ()
        do! readExpect netStream serverHello "serverHello"
        do! netStream.WriteAsync (clientRequest, 0, clientRequest.Length) |> Async.AwaitTask
        do! readExpect netStream serverHello "serverHello"

        let totalDuration = Stopwatch.StartNew ()
        use token = new CancellationTokenSource ()
        let readTask = startReadingPayload netStream token.Token |> Async.StartAsTask
        let writeTask = startWritingPayload netStream token.Token |> Async.StartAsTask
        do! Async.Sleep (TimeSpan.FromSeconds (float target.TestDurationSec))
        do! token.CancelAsync () |> Async.AwaitTask
        do! Task.WhenAll (readTask, writeTask) :> Task |> Async.AwaitTask
        totalDuration.Stop ()

        netStream.Close ()
        tcpClient.Close ()

        let totalSec = totalDuration.Elapsed.TotalSeconds
        let readBytes = readTask.Result.Bytes
        let writeBytes = writeTask.Result.Bytes
        return { Target = target
                 Time = DateTimeOffset.UtcNow
                 ReceivePerSec = ByteSize.FromBytes (readBytes / totalSec)
                 TransmitPerSec = ByteSize.FromBytes (writeBytes / totalSec) }
    }
