# netspeedexporter

Utility which exports results of `iperf3` utility as Prometheus metrics.

### Building

Standard .NET way of building applications.

```
dotnet build netspeedexporter.fsproj -c Release --self-contained --runtime <linux-x64|win-x64>
dotnet publish netspeedexporter.fsproj -c Release --self-contained --runtime <linux-x64|win-x64> -p:PublishSingleFile=true -o publish
```

### Usage

First, create config file. You can get sample by running `netspeedexporter --sample`

```json
[
  {
    "MetricPrefix": "host1",
    "Host": "host1.lan",
    "Port": 5201,
    "TestDurationSec": 5,
    "Speedometer": "iperf3"
  },
  {
    "MetricPrefix": "host2",
    "Host": "host2.lan",
    "Port": 2000,
    "TestDurationSec": 5,
    "Speedometer": "mikrotik"
  }
]
```

Config is a JSON array of targets(json objects). Network speed with each target will be computed and
results exported to Prometheus to scrap on `/metrics` path.

`Speedometer` can be `iperf3` or `mikrotik`.

In case of `iperf3`, command line utility [iperf3](https://github.com/esnet/iperf) should be available in PATH.
`netspeedexporter` will run it for each target.

In case of `mikrotik`, target host (which usually is a RouterOS powered device) must have `/tool/bandwidth-server` enabled.
By default, it listens on port 2000. Authentication must be disabled. `netspeedexporter` will use selfmade(and poor) implementation of MikroTik bandwidth protocol which is limited to:

- TCP protocol;
- 1 connection count;
- direction both;

Next, run exe with these arguments:

```
netspeedexproter --run --listen http://localhost:5000 --timerinterval 00:01:00 --config <path-to-json-file>
```

This will start built-in HTTP server on `localhost:5000`. Next, every 1 minute(00:01:00) network speed value to each target from current host will be measured (synchronously) and metrics on `/metrics` updated.

You can use `netspeedexporter --help` to see full usage help.

Thanks to the authors of this [repository](https://github.com/samm-git/btest-opensource) for reverse engineering of MikroTik bandwidth protocol.
