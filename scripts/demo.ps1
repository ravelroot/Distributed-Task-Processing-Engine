param(
    [int] $Count = 40,
    [string] $Url = ''
)

$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)

$values = @{}
if (Test-Path '.env') {
    foreach ($line in Get-Content '.env') {
        if ($line -match '^([A-Z_]+)=(.*)$') {
            $values[$Matches[1]] = $Matches[2]
        }
    }
}

if (-not $Url) {
    $port = if ($values['TURNO_PORT']) { $values['TURNO_PORT'] } else { '8080' }
    $Url = "http://localhost:$port"
}

$templates = @(
    @{ type = 'report.generate'; queue = 'reports'; payload = @{ rows = 400000; segments = 12 } },
    @{ type = 'file.process'; payload = @{ sizeMb = 48; chunkMb = 4; delayPerChunkMs = 250 } },
    @{ type = 'integration.sync'; payload = @{ records = 80; failureRate = 0.35; latencyMs = 600 } },
    @{ type = 'notification.send'; queue = 'critical'; payload = @{ to = 'pessoa@example.com'; channel = 'email'; subject = 'Teste' } },
    @{ type = 'file.process'; delaySeconds = 20; payload = @{ sizeMb = 24; chunkMb = 4; delayPerChunkMs = 200 } }
)

$jobs = for ($i = 1; $i -le $Count; $i++) { $templates[$i % $templates.Count] }
$jobs += @{ type = 'integration.sync'; maxAttempts = 2; payload = @{ records = 1; failureRate = 1; latencyMs = 100 } }

$body = @{ jobs = $jobs } | ConvertTo-Json -Depth 6
$headers = @{ 'X-Turno-Key' = $values['TURNO_API_KEY'] }
Invoke-RestMethod -Method Post -Uri "$Url/api/jobs/batch" -ContentType 'application/json' -Headers $headers -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) | Out-Null
Write-Host "$($jobs.Count) tarefas enviadas para $Url. Acompanhe no painel."
