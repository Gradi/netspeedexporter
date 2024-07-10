module netspeedexporter.Program

open Argu
open FSharp.Json
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Serilog
open Serilog.Events
open System
open System.IO
open System.Threading.Tasks
open netspeedexporter.NetSpeed


type ExporterArgs =
    | [<Mandatory>] Listen of string
    | [<Mandatory>] TimerInterval of string
    | [<Mandatory>] Config of string

    interface IArgParserTemplate with
        member this.Usage =
            match this with
            | Listen _ -> "Url to listen for incoming HTTP connections."
            | TimerInterval _ -> "Timer interval in valid 'TimeSpan.Parse' format. For example, 00:01:00.000 for 1 minute."
            | Config _ -> "Path to config file which specifies targets to test."

type CliArgs =
    | Run of ParseResults<ExporterArgs>
    | [<AltCommandLine("--sample")>] GenerateSampleConfig

    interface IArgParserTemplate with

        member this.Usage =
            match this with
            | Run _ -> "Runs exporter which will periodically measure lan speeds and export metrics via '/metrics'"
            | GenerateSampleConfig -> "Prints to stdout config sample"


let makeSampleConfig () =
    let sample = [|
        { MetricPrefix = "host1"; Host = "host1.lan"; Port = 5201; TestDurationSec = 5; Speedometer = Iperf3 }
        { MetricPrefix = "host2"; Host = "host2.lan"; Port = 2000; TestDurationSec = 5; Speedometer = Mikrotik }
    |]

    let json = Json.serializeEx (JsonConfig.create (unformatted = false)) sample
    printfn "%s" json


let readTargets (config: string) =
    if not (File.Exists config) then
        failwithf "Config file not found: %s" config

    let json = File.ReadAllText config
    Json.deserialize<Target list> json


let makeLogger () =
    let logger =
        LoggerConfiguration()
            .WriteTo.Console()
            .MinimumLevel.Is(LogEventLevel.Information)
            .CreateLogger ()
    Serilog.Log.Logger <- logger
    logger


let makeWebAppBuilder (listenUrl: string) =
    let builder = WebApplication.CreateBuilder()
    builder.Configuration.Sources.Clear ()
    builder.Configuration.AddCommandLine [|  |] |> ignore
    builder.WebHost.UseUrls([| listenUrl |]) |> ignore
    builder.WebHost.ConfigureKestrel((fun opt -> opt.AllowSynchronousIO <- true)) |> ignore
    builder.WebHost.ConfigureLogging ((fun log ->
        log.ClearProviders()
           .AddConsole()
           .SetMinimumLevel LogLevel.Warning |> ignore)) |> ignore
    builder.Host.UseConsoleLifetime () |> ignore

    builder



[<EntryPoint>]
let main args =
    try
        let commands = ArgumentParser.Create<CliArgs>().ParseCommandLine args
        match commands.Contains GenerateSampleConfig with
        | true ->
            makeSampleConfig ()
            0
        | false ->

            match commands.TryGetResult Run with
            | Some runCommandArgs ->

                let targets = readTargets (runCommandArgs.GetResult Config)
                let listenUrl = runCommandArgs.GetResult Listen
                let logger = makeLogger ()
                let measurer = NetSpeedMeasurer (targets, runCommandArgs.GetResult TimerInterval |> TimeSpan.Parse, logger.ForContext<NetSpeedMeasurer>())
                use app = (makeWebAppBuilder listenUrl).Build ()

                app.MapGet("/metrics", (fun ctx ->
                    async {
                        let currentResults = measurer.GetCurrentResults ()

                        ctx.Response.StatusCode <- StatusCodes.Status200OK
                        ctx.Response.ContentType <- "text/plain; charset=utf-8"
                        use writer = new StreamWriter (ctx.Response.Body)
                        for speed in currentResults do
                            let rx = sprintf "%s_%s" speed.Target.MetricPrefix "receive"
                            let tx = sprintf "%s_%s" speed.Target.MetricPrefix "transmit"
                            let timeMs = speed.Time.ToUnixTimeMilliseconds ()

                            fprintf writer "# TYPE %s gauge\n" rx
                            fprintf writer "%s %.3f %d\n" rx (speed.ReceivePerSec.MebiBytes * 8.0) timeMs
                            fprintf writer "# TYPE %s gauge\n" tx
                            fprintf writer "%s %.3f %d\n" tx (speed.TransmitPerSec.MebiBytes * 8.0) timeMs

                    }
                    |> Async.StartImmediateAsTask :> Task))
                    |> ignore


                app.Run()
                0

            | None ->
                eprintfn "Run me with \'--help\' to get help"
                1

    with
    | :? ArguParseException as exc ->
        eprintfn "%s" exc.Message
        1
    | exc ->
        eprintfn "%O" exc
        1
