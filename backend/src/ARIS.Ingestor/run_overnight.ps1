$ProjectDir = $PSScriptRoot
$MaxRetries = 100
$RetryDelaySec = 60
$CheckpointFile = Join-Path $ProjectDir "bin\Debug\net10.0\aris_ingestor_checkpoint.json"
$LogFile = Join-Path $ProjectDir "overnight_run.log"

function Write-Log($msg) {
    $line = "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $msg"
    Write-Host $line
    Add-Content -Path $LogFile -Value $line
}

Set-Location $ProjectDir

Write-Log "Starting overnight ingestion run."

for ($attempt = 1; $attempt -le $MaxRetries; $attempt++) {
    Write-Log "=== Attempt $attempt / $MaxRetries ==="

    if ($attempt -eq 1) {
        Write-Log "Running full fresh ingestion (phases 1-5)..."
        dotnet run -- --fresh
    } elseif (Test-Path $CheckpointFile) {
        $cp = Get-Content $CheckpointFile | ConvertFrom-Json
        Write-Log "Checkpoint found: Step=$($cp.Step), Timestamp=$($cp.Timestamp). Resuming ontology enrichment only."
        dotnet run -- --optimize-graph
    } else {
        Write-Log "No checkpoint found after failure. Re-running full ingestion (idempotent)."
        dotnet run --
    }

    $exitCode = $LASTEXITCODE

    if ($exitCode -eq 0) {
        Write-Log "Completed successfully on attempt $attempt."
        exit 0
    }

    Write-Log "Process exited with code $exitCode."

    if ($attempt -lt $MaxRetries) {
        Write-Log "Waiting ${RetryDelaySec}s before retry..."
        Start-Sleep -Seconds $RetryDelaySec
    }
}

Write-Log "All $MaxRetries attempts exhausted. Manual intervention required."
exit 1
