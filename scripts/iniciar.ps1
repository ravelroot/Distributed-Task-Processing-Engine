$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Host 'Instale o Docker Desktop e rode de novo.' -ForegroundColor Yellow
    exit 1
}

function New-Secret([int] $Length = 32) {
    $chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'.ToCharArray()
    $bytes = New-Object byte[] $Length
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($bytes)
    $rng.Dispose()
    -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] })
}

$envFile = Join-Path (Get-Location) '.env'
if (-not (Test-Path $envFile)) {
    Copy-Item '.env.example' $envFile
    Write-Host 'Arquivo .env criado a partir do .env.example.'
}

$generated = @{
    'TURNO_POSTGRES_PASSWORD' = 32
    'TURNO_RABBITMQ_PASSWORD' = 32
    'TURNO_REDIS_PASSWORD'    = 32
    'TURNO_API_KEY'           = 40
}

$lines = Get-Content $envFile | ForEach-Object {
    $line = $_
    foreach ($key in $generated.Keys) {
        if ($line -eq "$key=") {
            $line = "$key=$(New-Secret $generated[$key])"
        }
    }
    $line
}
[System.IO.File]::WriteAllLines($envFile, $lines, (New-Object System.Text.UTF8Encoding $false))

$values = @{}
foreach ($line in $lines) {
    if ($line -match '^([A-Z_]+)=(.*)$') {
        $values[$Matches[1]] = $Matches[2]
    }
}
$port = if ($values['TURNO_PORT']) { $values['TURNO_PORT'] } else { '8080' }

docker compose up -d --build
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host -NoNewline 'Aguardando a API ficar pronta'
$ready = $false
for ($i = 0; $i -lt 90; $i++) {
    try {
        Invoke-WebRequest -UseBasicParsing -TimeoutSec 3 "http://127.0.0.1:$port/health/ready" | Out-Null
        $ready = $true
        break
    }
    catch {
        Write-Host -NoNewline '.'
        Start-Sleep -Seconds 2
    }
}
Write-Host ''

if (-not $ready) {
    Write-Host 'A API não respondeu em 3 minutos. Veja os logs com: docker compose logs api' -ForegroundColor Yellow
    exit 1
}

Write-Host "Painel:          http://localhost:$port"
Write-Host "Documentação:    http://localhost:$port/docs"
Write-Host "RabbitMQ:        http://localhost:$($values['TURNO_RABBITMQ_UI_PORT'])"
if ($values['TURNO_API_KEY']) {
    Write-Host "Chave de acesso: $($values['TURNO_API_KEY'])"
}

Start-Process "http://localhost:$port"
