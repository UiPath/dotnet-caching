[CmdletBinding(PositionalBinding = $false)]
param(
    [switch]$NoBuild,
    [switch]$UseShardedRedis,
    [switch]$UseSingleMachine,
    [switch]$NoOpenTelemetry,
    [switch]$NoRedisInsight,
    [int]$StartupTimeoutSeconds = 240,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$BenchmarkArgs
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$defaultBenchmarkArgs = @(
    '--filter',
    '*ReadKnownKeys*',
    '--job',
    'Dry',
    '--warmupCount',
    '1',
    '--iterationCount',
    '1'
)

if ($null -eq $BenchmarkArgs -or $BenchmarkArgs.Count -eq 0) {
    $BenchmarkArgs = $defaultBenchmarkArgs
}
elseif ($BenchmarkArgs[0] -eq '--') {
    $BenchmarkArgs = $BenchmarkArgs | Select-Object -Skip 1
}

$logPrefix = Join-Path ([IO.Path]::GetTempPath()) "caching-aspire-benchmark-$PID"
$appHostOut = "$logPrefix-apphost.out.log"
$appHostErr = "$logPrefix-apphost.err.log"
$benchmarkOut = "$logPrefix-benchmark.out.log"

function Set-ScopedEnvironmentVariable {
    param(
        [hashtable]$OriginalValues,
        [string]$Name,
        [string]$Value
    )

    if (-not $OriginalValues.ContainsKey($Name)) {
        $OriginalValues[$Name] = [Environment]::GetEnvironmentVariable($Name, 'Process')
    }

    [Environment]::SetEnvironmentVariable($Name, $Value, 'Process')
}

function Restore-EnvironmentVariables {
    param([hashtable]$OriginalValues)

    foreach ($entry in $OriginalValues.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
}

function Get-DockerPublishedPort {
    param(
        [string]$NamePrefix,
        [int]$ContainerPort,
        [string[]]$ExcludedContainerIds = @()
    )

    $containerIds = docker ps --filter "name=$NamePrefix" --format '{{.ID}}'
    if ($LASTEXITCODE -ne 0 -or $null -eq $containerIds) {
        return $null
    }

    $candidates = @()
    foreach ($containerId in $containerIds) {
        if ([string]::IsNullOrWhiteSpace($containerId)) {
            continue
        }

        if ($ExcludedContainerIds -contains $containerId) {
            continue
        }

        $inspect = docker inspect $containerId | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or $null -eq $inspect) {
            continue
        }

        $container = if ($inspect -is [array]) { $inspect[0] } else { $inspect }
        $name = $container.Name.TrimStart('/')
        if (-not $name.StartsWith($NamePrefix, [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $portKey = "$ContainerPort/tcp"
        $portProperty = $container.NetworkSettings.Ports.PSObject.Properties[$portKey]
        if ($null -eq $portProperty -or $null -eq $portProperty.Value -or $portProperty.Value.Count -eq 0) {
            continue
        }

        $password = ($container.Config.Env | Where-Object { $_.StartsWith('REDIS_PASSWORD=', [StringComparison]::Ordinal) } | Select-Object -First 1)
        if (-not [string]::IsNullOrEmpty($password)) {
            $password = $password.Substring('REDIS_PASSWORD='.Length)
        }

        $candidates += [pscustomobject]@{
            Id = $containerId
            Name = $name
            Created = [datetime]$container.Created
            HostName = if ([string]::IsNullOrWhiteSpace($portProperty.Value[0].HostIp) -or $portProperty.Value[0].HostIp -eq '0.0.0.0' -or $portProperty.Value[0].HostIp -eq '::') { '127.0.0.1' } else { $portProperty.Value[0].HostIp }
            HostIp = $portProperty.Value[0].HostIp
            HostPort = [int]$portProperty.Value[0].HostPort
            Password = $password
        }
    }

    return $candidates | Sort-Object Created -Descending | Select-Object -First 1
}

function Get-RunningDockerContainerIds {
    $containerIds = docker ps --format '{{.ID}}'
    if ($LASTEXITCODE -ne 0 -or $null -eq $containerIds) {
        return @()
    }

    return @($containerIds | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function New-RedisCommand {
    param([string[]]$Parts)

    $builder = [Text.StringBuilder]::new()
    [void]$builder.Append('*').Append($Parts.Count).Append("`r`n")
    foreach ($part in $Parts) {
        [void]$builder.Append('$').Append([Text.Encoding]::UTF8.GetByteCount($part)).Append("`r`n")
        [void]$builder.Append($part).Append("`r`n")
    }

    return $builder.ToString()
}

function Test-RedisHostPing {
    param(
        [string]$HostName,
        [int]$Port,
        [string]$Password
    )

    $client = [Net.Sockets.TcpClient]::new()
    try {
        $connect = $client.BeginConnect($HostName, $Port, $null, $null)
        if (-not $connect.AsyncWaitHandle.WaitOne(1000)) {
            return $false
        }

        $client.EndConnect($connect)
        $client.SendTimeout = 1000
        $client.ReceiveTimeout = 1000

        $command = ''
        if (-not [string]::IsNullOrEmpty($Password)) {
            $command += New-RedisCommand @('AUTH', $Password)
        }

        $command += New-RedisCommand @('PING')
        $bytes = [Text.Encoding]::UTF8.GetBytes($command)
        $stream = $client.GetStream()
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush()

        $buffer = New-Object byte[] 1024
        $response = [Text.StringBuilder]::new()
        for ($i = 0; $i -lt 5; $i++) {
            try {
                $read = $stream.Read($buffer, 0, $buffer.Length)
                if ($read -le 0) {
                    return $false
                }

                [void]$response.Append([Text.Encoding]::UTF8.GetString($buffer, 0, $read))
                $text = $response.ToString()
                if ($text -match '\+PONG') {
                    return $true
                }

                if ($text -match '^-ERR|WRONGPASS|NOAUTH') {
                    return $false
                }
            }
            catch [IO.IOException] {
            }

            Start-Sleep -Milliseconds 100
        }

        return $false
    }
    catch {
        return $false
    }
    finally {
        $client.Dispose()
    }
}

function Wait-RedisContainer {
    param(
        [string]$NamePrefix,
        [int]$ContainerPort,
        [int]$TimeoutSeconds,
        [System.Diagnostics.Process]$AppHostProcess,
        [string[]]$ExcludedContainerIds = @()
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if ($AppHostProcess.HasExited) {
            throw "AppHost exited early with code $($AppHostProcess.ExitCode)."
        }

        $publishedPort = Get-DockerPublishedPort -NamePrefix $NamePrefix -ContainerPort $ContainerPort -ExcludedContainerIds $ExcludedContainerIds
        if ($null -ne $publishedPort) {
            $redisCliArgs = @('exec', $publishedPort.Id, 'redis-cli', '--no-auth-warning', '-p', $ContainerPort)
            if (-not [string]::IsNullOrEmpty($publishedPort.Password)) {
                $redisCliArgs += @('-a', $publishedPort.Password)
            }

            $redisCliArgs += 'ping'
            $ping = docker @redisCliArgs 2>$null
            if ($LASTEXITCODE -eq 0 -and ($ping -join "`n") -match 'PONG') {
                if (Test-RedisHostPing -HostName $publishedPort.HostName -Port $publishedPort.HostPort -Password $publishedPort.Password) {
                    Write-Host "Redis ready: $($publishedPort.Name) -> $($publishedPort.HostName):$($publishedPort.HostPort)"
                    return $publishedPort
                }
            }
        }

        Start-Sleep -Seconds 1
    }

    throw "Timed out waiting for Redis container '$NamePrefix' on port $ContainerPort."
}

$originalEnvironment = @{}
$appHost = $null

try {
    Push-Location $repoRoot

    if (-not $NoBuild) {
        dotnet build samples/UiPath.Caching.Sample.AppHost/UiPath.Caching.Sample.AppHost.csproj
        if ($LASTEXITCODE -ne 0) {
            throw "AppHost build failed with exit code $LASTEXITCODE."
        }

        dotnet build benchmarks/UiPath.Caching.Benchmarks/UiPath.Caching.Benchmarks.csproj -c Release
        if ($LASTEXITCODE -ne 0) {
            throw "Benchmark build failed with exit code $LASTEXITCODE."
        }
    }

    Set-ScopedEnvironmentVariable $originalEnvironment 'SampleAspNetCore__UseShardedRedis' $UseShardedRedis.IsPresent.ToString().ToLowerInvariant()
    Set-ScopedEnvironmentVariable $originalEnvironment 'SampleAspNetCore__UseSingleMachine' $UseSingleMachine.IsPresent.ToString().ToLowerInvariant()
    Set-ScopedEnvironmentVariable $originalEnvironment 'SampleAspNetCore__UseOpenTelemetry' (-not $NoOpenTelemetry.IsPresent).ToString().ToLowerInvariant()
    Set-ScopedEnvironmentVariable $originalEnvironment 'SampleAspNetCore__UseRedisInsight' (-not $NoRedisInsight.IsPresent).ToString().ToLowerInvariant()

    $appHostArgs = @(
        'run',
        '--project',
        'samples/UiPath.Caching.Sample.AppHost/UiPath.Caching.Sample.AppHost.csproj',
        '--launch-profile',
        'http'
    )

    if ($NoBuild) {
        $appHostArgs += '--no-build'
    }

    $preExistingDockerContainers = Get-RunningDockerContainerIds
    if ($preExistingDockerContainers.Count -gt 0) {
        Write-Host "Ignoring $($preExistingDockerContainers.Count) pre-existing Docker container(s) while discovering AppHost resources."
    }

    $startArgs = @{
        FilePath               = 'dotnet'
        ArgumentList           = $appHostArgs
        WorkingDirectory       = $repoRoot
        RedirectStandardOutput = $appHostOut
        RedirectStandardError  = $appHostErr
        PassThru               = $true
    }
    if ($IsWindows) {
        $startArgs.WindowStyle = 'Hidden'
    }
    $appHost = Start-Process @startArgs

    if ($UseShardedRedis) {
        $redisEndpoints = @(
            Wait-RedisContainer -NamePrefix 'redis-master1-' -ContainerPort 6379 -TimeoutSeconds $StartupTimeoutSeconds -AppHostProcess $appHost -ExcludedContainerIds $preExistingDockerContainers
            Wait-RedisContainer -NamePrefix 'redis-slave1-master1-' -ContainerPort 6380 -TimeoutSeconds $StartupTimeoutSeconds -AppHostProcess $appHost -ExcludedContainerIds $preExistingDockerContainers
            Wait-RedisContainer -NamePrefix 'redis-slave2-master1-' -ContainerPort 6381 -TimeoutSeconds $StartupTimeoutSeconds -AppHostProcess $appHost -ExcludedContainerIds $preExistingDockerContainers
            Wait-RedisContainer -NamePrefix 'redis-master2-' -ContainerPort 6382 -TimeoutSeconds $StartupTimeoutSeconds -AppHostProcess $appHost -ExcludedContainerIds $preExistingDockerContainers
            Wait-RedisContainer -NamePrefix 'redis-slave1-master2-' -ContainerPort 6383 -TimeoutSeconds $StartupTimeoutSeconds -AppHostProcess $appHost -ExcludedContainerIds $preExistingDockerContainers
            Wait-RedisContainer -NamePrefix 'redis-slave2-master2-' -ContainerPort 6384 -TimeoutSeconds $StartupTimeoutSeconds -AppHostProcess $appHost -ExcludedContainerIds $preExistingDockerContainers
        ) | ForEach-Object { "$($_.HostName):$($_.HostPort)" }

        Set-ScopedEnvironmentVariable $originalEnvironment 'Caching__ShardKeyEnabled' 'true'
        Set-ScopedEnvironmentVariable $originalEnvironment 'Caching__Connections__Redis__ConnectionString' ($redisEndpoints -join ',')
        Set-ScopedEnvironmentVariable $originalEnvironment 'Caching__Connections__Redis__ConnectionStringExtraParams' 'allowAdmin=true,abortConnect=false,ssl=false,connectRetry=5,keepAlive=30,name=test,syncTimeout=10000,connectTimeout=10000'
    }
    else {
        $redisEndpoint = Wait-RedisContainer -NamePrefix 'cache-' -ContainerPort 6380 -TimeoutSeconds $StartupTimeoutSeconds -AppHostProcess $appHost -ExcludedContainerIds $preExistingDockerContainers
        $redisConnectionString = "$($redisEndpoint.HostName):$($redisEndpoint.HostPort),abortConnect=false,ssl=false"
        if (-not [string]::IsNullOrEmpty($redisEndpoint.Password)) {
            $redisConnectionString = "$redisConnectionString,password=$($redisEndpoint.Password)"
        }

        Set-ScopedEnvironmentVariable $originalEnvironment 'Caching__ShardKeyEnabled' 'false'
        Set-ScopedEnvironmentVariable $originalEnvironment 'Caching__Connections__Redis__ConnectionString' $redisConnectionString
        Set-ScopedEnvironmentVariable $originalEnvironment 'Caching__Connections__Redis__ConnectionStringExtraParams' 'allowAdmin=true,connectRetry=5,keepAlive=30,syncTimeout=10000,connectTimeout=10000'
    }

    $benchmarkCommand = @(
        'run',
        '--project',
        'benchmarks/UiPath.Caching.Benchmarks/UiPath.Caching.Benchmarks.csproj',
        '--framework',
        'net10.0',
        '-c',
        'Release'
    )

    if ($NoBuild) {
        $benchmarkCommand += '--no-build'
    }

    $benchmarkCommand += '--'
    $benchmarkCommand += $BenchmarkArgs

    dotnet @benchmarkCommand 2>&1 | Tee-Object -FilePath $benchmarkOut
    $benchmarkExitCode = $LASTEXITCODE
    if ($benchmarkExitCode -ne 0) {
        throw "Benchmark run failed with exit code $benchmarkExitCode."
    }

    # Heuristic safety net: BenchmarkDotNet can exit 0 yet produce no results (e.g. all benchmarks
    # filtered out, or a build error swallowed at the inner level). String-match the output for
    # known no-result signatures so the wrapper fails loudly. False-positive risk if a benchmark
    # name ever contains one of these substrings — acceptable trade-off for the current set.
    $benchmarkText = Get-Content -LiteralPath $benchmarkOut -Raw
    if ($benchmarkText -match 'Build Error' `
        -or $benchmarkText -match 'executed benchmarks:\s*0' `
        -or $benchmarkText -match 'Benchmarks with issues' `
        -or $benchmarkText -match 'There are not any results runs' `
        -or $benchmarkText -match 'No Workload Results') {
        throw "BenchmarkDotNet completed without valid benchmark results. See $benchmarkOut."
    }
}
finally {
    if ($null -ne $appHost -and -not $appHost.HasExited) {
        # Try graceful stop so Aspire can tear down its containers; fall back to -Force if it lingers.
        Stop-Process -Id $appHost.Id -ErrorAction SilentlyContinue
        try { Wait-Process -Id $appHost.Id -Timeout 10 -ErrorAction Stop } catch {}
        if (-not $appHost.HasExited) {
            Stop-Process -Id $appHost.Id -Force -ErrorAction SilentlyContinue
        }
    }

    Restore-EnvironmentVariables $originalEnvironment
    Pop-Location

    Write-Host "AppHost stdout: $appHostOut"
    Write-Host "AppHost stderr: $appHostErr"
    Write-Host "Benchmark output: $benchmarkOut"
}
