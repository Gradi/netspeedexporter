namespace netspeedexporter

open System
open ByteSizeLib
open FSharp.Json

type Speedometer =
    | [<JsonUnionCase("iperf3")>]  Iperf3
    | [<JsonUnionCase("mikrotik")>] Mikrotik

type Target =
    { MetricPrefix: string
      Host: string
      Port: int
      TestDurationSec: int
      Speedometer: Speedometer }

[<NoComparison;NoEquality>]
type NetSpeedResult =
    { Target: Target
      Time: DateTimeOffset
      ReceivePerSec: ByteSize
      TransmitPerSec: ByteSize }


