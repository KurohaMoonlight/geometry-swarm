Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "     Building Geometry Swarm Installer    " -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

$isccPath = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $isccPath)) {
    $isccCmd = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($isccCmd) {
        $isccPath = $isccCmd.Source
    } else {
        Write-Error "Inno Setup compiler (ISCC.exe) not found!"
        exit 1
    }
}

$godotPath = "E:\godot\Godot_v4.7.2-stable_mono_win64_console.exe"
if (-not (Test-Path $godotPath)) {
    $godotPath = "E:\godot\Godot_v4.7.2-stable_mono_win64.exe"
}

Write-Host "Step 1: Building .NET C# Solution..." -ForegroundColor Yellow
dotnet build -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet build failed!"
    exit 1
}

Write-Host "Step 2: Exporting Godot Release Project..." -ForegroundColor Yellow
if (Test-Path "build") {
    Remove-Item -Recurse -Force "build"
}
New-Item -ItemType Directory -Path "build" | Out-Null

& "$godotPath" --headless --export-release "geometry_swarm" "build\GeometrySwarm.exe"
if ($LASTEXITCODE -ne 0) {
    Write-Error "Godot export failed!"
    exit 1
}

Write-Host "Step 2.5: Bundling GodotSharp .NET assemblies..." -ForegroundColor Yellow
$godotSharpApi = Join-Path (Split-Path $godotPath -Parent) "GodotSharp\Api"
if (Test-Path $godotSharpApi) {
    New-Item -ItemType Directory -Path "build\GodotSharp\Api\Debug" -Force | Out-Null
    New-Item -ItemType Directory -Path "build\GodotSharp\Api\Release" -Force | Out-Null
    Copy-Item "$godotSharpApi\Debug\*.dll" "build\GodotSharp\Api\Debug\" -Force
    Copy-Item "$godotSharpApi\Debug\*.json" "build\GodotSharp\Api\Debug\" -Force
    Copy-Item "$godotSharpApi\Release\*.dll" "build\GodotSharp\Api\Release\" -Force
    Copy-Item "$godotSharpApi\Release\*.json" "build\GodotSharp\Api\Release\" -Force
    Write-Host "GodotSharp assemblies bundled successfully." -ForegroundColor Green
} else {
    Write-Warning "GodotSharp\Api directory not found at $godotSharpApi!"
}

Write-Host "Step 2.6: Bundling Godot Mono project assembly load paths..." -ForegroundColor Yellow
New-Item -ItemType Directory -Path "build\.godot\mono\temp\bin\Release" -Force | Out-Null
New-Item -ItemType Directory -Path "build\.godot\mono\temp\bin\Debug" -Force | Out-Null
Copy-Item "build\data_GeometrySwarm_windows_x86_64\*" "build\.godot\mono\temp\bin\Release\" -Force
Copy-Item "build\data_GeometrySwarm_windows_x86_64\*" "build\.godot\mono\temp\bin\Debug\" -Force
Write-Host "Project assemblies mirrored to .godot/mono/temp/bin successfully." -ForegroundColor Green

Write-Host "Step 3: Creating Installer with Inno Setup..." -ForegroundColor Yellow
if (-not (Test-Path "installer")) {
    New-Item -ItemType Directory -Path "installer" | Out-Null
}

& "$isccPath" "installer.iss"
if ($LASTEXITCODE -eq 0) {
    Write-Host "==========================================" -ForegroundColor Green
    Write-Host " Installer Successfully Created in installer/! " -ForegroundColor Green
    Write-Host "==========================================" -ForegroundColor Green
    Get-Item "installer\GeometrySwarm_Setup_*.exe" | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize

    Write-Host "Cleaning up build directory..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force "build"
    Write-Host "Build folder deleted." -ForegroundColor Green
} else {
    Write-Error "Installer compilation failed!"
}
