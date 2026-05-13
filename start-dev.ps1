# Start all co-wildfire dev services: Docker, backend, frontend

# Docker
Write-Host "Starting Docker services..."
docker compose up -d

# Backend: kill any running instance, rebuild, restart
Write-Host "Stopping any running backend process..."
Get-Process -Name "CoWildfireApi" -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host "Building backend..."
Push-Location "$PSScriptRoot\backend"
dotnet build CoWildfireApi.sln
if ($LASTEXITCODE -ne 0) {
    Write-Error "Backend build failed. Aborting."
    Pop-Location
    exit 1
}

Write-Host "Starting backend..."
Start-Process -FilePath "dotnet" -ArgumentList "run", "--project", "CoWildfireApi\CoWildfireApi.csproj", "--no-build" -WindowStyle Normal
Pop-Location

# Frontend
Write-Host "Starting frontend..."
Push-Location "$PSScriptRoot\frontend"
Start-Process -FilePath "npm" -ArgumentList "run", "dev" -WindowStyle Normal
Pop-Location

Write-Host "All services started."
