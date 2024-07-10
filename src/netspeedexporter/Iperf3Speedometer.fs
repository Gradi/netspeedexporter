module netspeedexporter.Iperf3Speedometer

open System
open System.Diagnostics
open ByteSizeLib
open Newtonsoft.Json.Linq


let measureSpeed (target: Target) : Async<NetSpeedResult> =
    async {
        let procInfo = ProcessStartInfo("iperf3", [
            "--client"
            target.Host
            "--port"
            target.Port.ToString ()
            "--json"
            "--time"
            target.TestDurationSec.ToString ()
        ])
        procInfo.RedirectStandardOutput <- true
        procInfo.RedirectStandardError <- true

        use job = Process.Start(procInfo)
        let! outputJson = job.StandardOutput.ReadToEndAsync () |> Async.AwaitTask
        let! errorOutput = job.StandardError.ReadToEndAsync () |> Async.AwaitTask
        do! job.WaitForExitAsync() |> Async.AwaitTask

        if job.ExitCode <> 0 then
            failwithf "\"iperf3\" returned \"%d\" exit code.\nstdout was: %s\nstderr was: %s\n\n"
                job.ExitCode outputJson errorOutput

        let jObj = JObject.Parse outputJson
        let receive = jObj.SelectToken("end.sum_received.bits_per_second").ToObject<double>()
        let transmit = jObj.SelectToken("end.sum_sent.bits_per_second").ToObject<double>()

        return { Target = target; Time = DateTimeOffset.UtcNow;
                 ReceivePerSec = ByteSize.FromBits (int64 receive)
                 TransmitPerSec = ByteSize.FromBits (int64 transmit) }
    }

