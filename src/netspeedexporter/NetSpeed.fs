module netspeedexporter.NetSpeed

open ByteSizeLib
open Serilog
open System
open System.Diagnostics


let defaultResult (target: Target) =
    { Target = target; Time = DateTimeOffset.UtcNow
      ReceivePerSec = ByteSize.FromBits (1024L * 1024L * -8L); TransmitPerSec = ByteSize.FromBits (1024L * 1024L * -8L) }


let measureSpeed (target: Target) : Async<NetSpeedResult> =
    async {
        match  target.Speedometer with
        | Iperf3 -> return! Iperf3Speedometer.measureSpeed target
        | Mikrotik -> return! MikrotikSpeedometer.measureSpeed target (TimeSpan.FromSeconds 5.0)
    }



type NetSpeedMeasurer (targets: Target list, interval: TimeSpan, logger: ILogger) as this =

    let mutable currentResults = targets |> List.map defaultResult

    let runMeasurement (target: Target) =
        async {
             try
                 let stopwatch = Stopwatch.StartNew ()
                 logger.Information ("Testing speed with {Host}:{Port}", target.Host, target.Port)
                 let! result = measureSpeed target
                 logger.Information ("Done testing speed with {Host}:{Port} in {Elapsed}", target.Host, target.Port, stopwatch.Elapsed)
                 return result
             with
             | exc ->
                 logger.Error (exc, "Error on testing speed with {Host}:{Port}", target.Host, target.Port)
                 return defaultResult target
        }

    let runMeasurements (timer: System.Timers.Timer) =
        async {
            try
                try
                    let stopwatch = Stopwatch.StartNew ()
                    logger.Information ("Going to test speed with {TargetCount} targets", List.length targets)

                    let! newResults =
                        targets
                        |> Seq.ofList
                        |> Seq.map runMeasurement
                        |> MSeq.unwrapAsync

                    lock this (fun () -> currentResults <- newResults)

                    logger.Information ("Done testing {TargetCount} targets in {Elapsed}", List.length targets, stopwatch.Elapsed)

                with
                | exc ->
                    logger.Error (exc, "Error on testing all {TargetCount} targets", List.length targets)
                    lock this (fun () -> currentResults <- List.map defaultResult targets)
            finally
                timer.Start ()
        }

    let timer =
        let t = new System.Timers.Timer (interval)
        t.AutoReset <- false
        t.Elapsed.Add(fun _ -> runMeasurements t |> Async.StartAsTask |> ignore)
        t

    do
        runMeasurements timer |> Async.StartAsTask |> ignore


    member _.GetCurrentResults () =
        lock this (fun () -> currentResults)
